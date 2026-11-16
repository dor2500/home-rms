using System.Diagnostics;

namespace HomeRms.Agent.Linux.Core;

/// <summary>
/// Gatekeeper for interactive screen/input control on Linux.
/// Uses zenity or kdialog when a desktop session is present, or --auto-consent on headless servers.
/// </summary>
public sealed class ConsentCoordinator(ActivityLog log, bool autoConsent)
{
    private static readonly TimeSpan GrantDuration = TimeSpan.FromHours(1);
    private readonly object _gate = new();
    private DateTimeOffset _grantUntil = DateTimeOffset.MinValue;
    private string? _grantedOperator;

    public bool RequestGrant(string operatorName, string action)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _grantUntil &&
                string.Equals(_grantedOperator, operatorName, StringComparison.OrdinalIgnoreCase))
            {
                log.Write($"[consent] reusing active grant for '{operatorName}' ({action})");
                return true;
            }
        }

        if (autoConsent)
        {
            Grant(operatorName);
            return true;
        }

        var hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                         !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (!hasDisplay)
        {
            log.Write("[consent] denied — headless session without --auto-consent");
            return false;
        }

        log.Write($"[consent] prompting user: '{operatorName}' requests {action}");
        var allowed = AskViaGuiDialog(operatorName, action);
        if (allowed) Grant(operatorName);
        else log.Write($"[consent] DENIED for '{operatorName}'");
        return allowed;
    }

    private void Grant(string operatorName)
    {
        lock (_gate)
        {
            _grantUntil = DateTimeOffset.UtcNow + GrantDuration;
            _grantedOperator = operatorName;
        }
        log.Write($"[consent] GRANTED to '{operatorName}' until {_grantUntil:HH:mm}");
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
        log.Write("[consent] all grants revoked");
    }

    private static bool AskViaGuiDialog(string operatorName, string action)
    {
        var safeOp = new string(operatorName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        var msg = $"Home RMS Access Request: '{safeOp}' wants to {action} on this computer. Allow for 1 hour?";

        if (TryRunDialog("zenity", $"--question --title=\"Home RMS — Access Request\" --text=\"{msg}\" --ok-label=\"Allow\" --cancel-label=\"Deny\"", out var ok1))
            return ok1;

        if (TryRunDialog("kdialog", $"--title \"Home RMS — Access Request\" --yesno \"{msg}\"", out var ok2))
            return ok2;

        return false;
    }

    private static bool TryRunDialog(string tool, string args, out bool approved)
    {
        approved = false;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null) return false;
            proc.WaitForExit(30_000);
            approved = proc.ExitCode == 0;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
