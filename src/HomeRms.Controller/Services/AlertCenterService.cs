using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

namespace HomeRms.Controller.Services;

public enum AlertSeverity
{
    Critical,
    Warning,
    Info,
}

public sealed class AlertThresholds
{
    public double CpuThresholdPercent { get; set; } = 90;
    public double RamThresholdPercent { get; set; } = 90;
    public double DiskFreeMinPercent { get; set; } = 10;
    public double TempThresholdC { get; set; } = 85;
    public bool AlertOnOffline { get; set; } = true;
}

public sealed class AlertItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public required string EndpointName { get; init; }
    public required string Host { get; init; }
    public required AlertSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public bool Acknowledged { get; set; }

    public string TimeText => Timestamp.ToString("HH:mm:ss");
    public string SeverityText => Severity.ToString().ToUpperInvariant();

    public Brush SeverityBrush => Severity switch
    {
        AlertSeverity.Critical => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
        AlertSeverity.Warning => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
        _ => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
    };
}

/// <summary>
/// Evaluates endpoint telemetry against configurable thresholds and manages the
/// Alert Center queue (shared between Desktop Console and Mobile Web App).
/// </summary>
public sealed class AlertCenterService
{
    private static readonly string ThresholdsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HomeRMS", "alert-thresholds.json");

    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new();
    private readonly ConcurrentDictionary<string, bool> _wasOnline = new();

    public AlertThresholds Thresholds { get; private set; } = new();
    public ObservableCollection<AlertItem> Alerts { get; } = [];

    public int UnreadCount => Alerts.Count(a => !a.Acknowledged);

    public event Action? AlertsChanged;

    public AlertCenterService()
    {
        LoadThresholds();
    }

    public void LoadThresholds()
    {
        try
        {
            if (File.Exists(ThresholdsPath))
            {
                var loaded = JsonSerializer.Deserialize<AlertThresholds>(File.ReadAllText(ThresholdsPath));
                if (loaded is not null) Thresholds = loaded;
            }
        }
        catch { }
    }

    public void SaveThresholds()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThresholdsPath)!);
            File.WriteAllText(ThresholdsPath, JsonSerializer.Serialize(Thresholds, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void EvaluateSample(string endpointName, string host, bool isOnline, double cpuPercent, double ramPercent, double? minDiskFreePercent = null, double? maxTempC = null)
    {
        if (_wasOnline.TryGetValue(host, out var prevOnline) && prevOnline && !isOnline && Thresholds.AlertOnOffline)
        {
            Raise(endpointName, host, AlertSeverity.Critical, "Endpoint Offline",
                $"{endpointName} ({host}) stopped responding to health probes.", cooldownMinutes: 5);
        }
        _wasOnline[host] = isOnline;

        if (!isOnline) return;

        if (cpuPercent >= Thresholds.CpuThresholdPercent)
        {
            Raise(endpointName, host, AlertSeverity.Warning, "High CPU Utilization",
                $"CPU load at {cpuPercent:0}% (threshold {Thresholds.CpuThresholdPercent:0}%).", cooldownMinutes: 3);
        }

        if (ramPercent >= Thresholds.RamThresholdPercent)
        {
            Raise(endpointName, host, AlertSeverity.Warning, "High Memory Utilization",
                $"RAM usage at {ramPercent:0}% (threshold {Thresholds.RamThresholdPercent:0}%).", cooldownMinutes: 3);
        }

        if (minDiskFreePercent is < 100 && minDiskFreePercent.Value < Thresholds.DiskFreeMinPercent)
        {
            Raise(endpointName, host, AlertSeverity.Critical, "Low Disk Space",
                $"Free disk space dropped to {minDiskFreePercent.Value:0.0}% (min {Thresholds.DiskFreeMinPercent:0}%).", cooldownMinutes: 15);
        }

        if (maxTempC is > 0 && maxTempC.Value >= Thresholds.TempThresholdC)
        {
            Raise(endpointName, host, AlertSeverity.Critical, "Thermal Warning",
                $"Hardware temperature reached {maxTempC.Value:0}°C (limit {Thresholds.TempThresholdC:0}°C).", cooldownMinutes: 5);
        }
    }

    public void Raise(string endpointName, string host, AlertSeverity severity, string title, string detail, int cooldownMinutes = 2)
    {
        var key = $"{host}|{title}";
        var now = DateTimeOffset.Now;
        if (_cooldowns.TryGetValue(key, out var until) && now < until)
            return;
        _cooldowns[key] = now.AddMinutes(cooldownMinutes);

        var item = new AlertItem
        {
            EndpointName = endpointName,
            Host = host,
            Severity = severity,
            Title = title,
            Detail = detail,
            Timestamp = now,
        };

        void Insert()
        {
            Alerts.Insert(0, item);
            while (Alerts.Count > 200) Alerts.RemoveAt(Alerts.Count - 1);
            AlertsChanged?.Invoke();
        }

        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.BeginInvoke(Insert);
        else
            Insert();
    }

    public void Acknowledge(string id)
    {
        var item = Alerts.FirstOrDefault(a => a.Id == id);
        if (item is not null)
        {
            item.Acknowledged = true;
            AlertsChanged?.Invoke();
        }
    }

    public void AcknowledgeAll()
    {
        foreach (var a in Alerts) a.Acknowledged = true;
        AlertsChanged?.Invoke();
    }

    public void ClearAll()
    {
        Alerts.Clear();
        AlertsChanged?.Invoke();
    }
}
