namespace HomeRms.Agent.Linux.Core;

/// <summary>
/// Thread-safe, append-only record of everything the Linux agent does on behalf of a
/// remote operator. Surfaced live to stdout/journald.
/// </summary>
public sealed class ActivityLog
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (_gate) _lines.Add(line);
        Console.WriteLine(line);
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }
}
