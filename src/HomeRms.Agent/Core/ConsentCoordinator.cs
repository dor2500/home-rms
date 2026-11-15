namespace HomeRms.Agent.Core;

/// <summary>UI surface the tray provides so the coordinator can prompt and signal the user.</summary>
public interface IConsentUi
{
    /// <summary>Shows a modal prompt in the user's session. Returns true only on explicit Allow.</summary>
    bool AskControlConsent(string operatorName, string action);

    /// <summary>Raises/clears the persistent "your screen is being viewed" banner.</summary>
    void SetViewingActive(bool active, string? operatorName);
}

/// <summary>
/// Gatekeeper for every interactive-control action (screen view, input injection).
/// A grant is obtained by an explicit on-machine Allow and lasts a bounded time;
/// while anyone is viewing, a visible banner is shown. Read-only inventory and
/// shell commands are not silently allowed either — they are written to the log.
/// </summary>
public sealed class ConsentCoordinator
{
    private static readonly TimeSpan GrantDuration = TimeSpan.FromHours(1);

    private readonly ActivityLog _log;
    private readonly object _gate = new();
    private IConsentUi? _ui;
    private DateTimeOffset _grantUntil = DateTimeOffset.MinValue;
    private string? _grantedOperator;
    private int _activeViewers;

    public ConsentCoordinator(ActivityLog log) => _log = log;

    public void AttachUi(IConsentUi ui) => _ui = ui;

    /// <summary>
    /// Ensures an active control grant for <paramref name="operatorName"/>, prompting
    /// the user if none exists. Returns false if denied or no UI is available.
    /// </summary>
    public bool RequestGrant(string operatorName, string action)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _grantUntil &&
                string.Equals(_grantedOperator, operatorName, StringComparison.OrdinalIgnoreCase))
            {
                _log.Write($"[consent] reusing active grant for '{operatorName}' ({action})");
                return true;
            }
        }

        var ui = _ui;
        if (ui is null)
        {
            _log.Write("[consent] denied — no interactive session to confirm with");
            return false;
        }

        _log.Write($"[consent] prompting user: '{operatorName}' requests {action}");
        var allowed = ui.AskControlConsent(operatorName, action);
        if (allowed)
        {
            lock (_gate)
            {
                _grantUntil = DateTimeOffset.UtcNow + GrantDuration;
                _grantedOperator = operatorName;
            }
            _log.Write($"[consent] GRANTED to '{operatorName}' until {(_grantUntil):HH:mm}");
        }
        else
        {
            _log.Write($"[consent] DENIED for '{operatorName}'");
        }
        return allowed;
    }

    public bool HasActiveGrant(string operatorName)
    {
        lock (_gate)
            return DateTimeOffset.UtcNow < _grantUntil &&
                   string.Equals(_grantedOperator, operatorName, StringComparison.OrdinalIgnoreCase);
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            _grantUntil = DateTimeOffset.MinValue;
            _grantedOperator = null;
        }
        _log.Write("[consent] all grants revoked by user");
    }

    public IDisposable BeginViewing(string operatorName)
    {
        if (Interlocked.Increment(ref _activeViewers) == 1)
            _ui?.SetViewingActive(true, operatorName);
        _log.Write($"[screen] '{operatorName}' started viewing the screen");
        return new ViewingScope(this, operatorName);
    }

    private void EndViewing(string operatorName)
    {
        if (Interlocked.Decrement(ref _activeViewers) == 0)
            _ui?.SetViewingActive(false, null);
        _log.Write($"[screen] '{operatorName}' stopped viewing the screen");
    }

    private sealed class ViewingScope(ConsentCoordinator owner, string operatorName) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndViewing(operatorName);
        }
    }
}
