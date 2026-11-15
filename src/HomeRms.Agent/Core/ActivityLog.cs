namespace HomeRms.Agent.Core;

/// <summary>
/// Thread-safe, append-only record of everything the agent does on behalf of a
/// remote operator. It is surfaced live in the tray's activity window so the
/// person at the machine can always see what is happening. This visibility is a
/// core part of what makes the tool consent-based rather than covert.
/// </summary>
public sealed class ActivityLog
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();

    public event Action<string>? LineAdded;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (_gate) _lines.Add(line);
        LineAdded?.Invoke(line);
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }
}
