using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using HomeRms.Controller.Themes;

namespace HomeRms.Controller.Services;

/// <summary>One line in the Live Console drawer.</summary>
public sealed class LogEntry
{
    public required DateTime Time { get; init; }
    /// <summary>Console filter bucket: Activity, Deploy, Scripts, Alerts.</summary>
    public required string Source { get; init; }
    /// <summary>Finer tag shown in the row (CONNECT, winget, endpoint name…).</summary>
    public required string Category { get; init; }
    public required string Message { get; init; }
    public PaletteTone Tone { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");
    public Brush ToneBrush => Palette.Fg(Tone);
    public override string ToString() => $"{TimeText}  [{Source}/{Category}]  {Message}";
}

/// <summary>
/// Small in-process log bus feeding the Live Console drawer. Existing sinks (activity log,
/// deploy log, script runs, alert center) publish here in addition to their own views.
/// Safe to call from any thread; entries are appended on the UI dispatcher.
/// </summary>
public static class LogBus
{
    public const string Activity = "Activity";
    public const string Deploy = "Deploy";
    public const string Scripts = "Scripts";
    public const string Alerts = "Alerts";

    private const int Capacity = 2000;

    /// <summary>Oldest first; bound to the console list.</summary>
    public static ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>Raised on the UI thread after an entry was appended.</summary>
    public static event Action<LogEntry>? Published;

    public static void Publish(string source, string category, string message, PaletteTone tone = PaletteTone.Neutral)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new LogEntry { Time = DateTime.Now, Source = source, Category = category, Message = message.TrimEnd(), Tone = tone };

        void Append()
        {
            Entries.Add(entry);
            while (Entries.Count > Capacity) Entries.RemoveAt(0);
            Published?.Invoke(entry);
        }

        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Append();
        else d.BeginInvoke(Append);
    }

    public static void Clear() => Entries.Clear();

    /// <summary>Maps an activity-log category (CONNECT, ALERT, SCRIPT…) to its console source and tone.</summary>
    public static (string Source, PaletteTone Tone) Classify(string category) => category.ToUpperInvariant() switch
    {
        "ALERT" => (Alerts, PaletteTone.Danger),
        "SCRIPT" or "MASS RUN" => (Scripts, PaletteTone.Accent),
        "CONNECT" or "MTLS" => (Activity, PaletteTone.Success),
        "LAN SCAN" or "FLEET" => (Activity, PaletteTone.Info),
        _ => (Activity, PaletteTone.Neutral),
    };
}
