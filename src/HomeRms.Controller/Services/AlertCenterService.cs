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

public enum AlertCategory
{
    General,
    EndpointOffline,
    HighCpu,
    HighMemory,
    LowDisk,
    Thermal,
    BlockedSite,
    AgentWarning,
    ControllerUpdate,
}

public sealed class AlertThresholds
{
    public double CpuThresholdPercent { get; set; } = 90;
    public double RamThresholdPercent { get; set; } = 90;
    public double DiskFreeMinPercent { get; set; } = 10;
    public double TempThresholdC { get; set; } = 85;

    // Selectable alert categories
    public bool AlertOnOffline { get; set; } = true;
    public bool AlertOnHighCpu { get; set; } = true;
    public bool AlertOnHighRam { get; set; } = true;
    public bool AlertOnLowDisk { get; set; } = true;
    public bool AlertOnThermal { get; set; } = true;
    public bool AlertOnBlockedSite { get; set; } = true;
    public bool AlertOnAgentWarning { get; set; } = true;
    public bool AlertOnControllerUpdate { get; set; } = true;

    // Optional notifications
    public bool DesktopToastEnabled { get; set; }
    public bool SoundEnabled { get; set; }

    // Background LAN scan & auto-connect for alerts
    public bool AutoScanLanEnabled { get; set; }
    public int AutoScanIntervalSeconds { get; set; } = 30;
    public bool AutoConnectForAlerts { get; set; } = true;
}

public sealed class AlertItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public required string EndpointName { get; init; }
    public required string Host { get; init; }
    public required AlertSeverity Severity { get; init; }
    public AlertCategory Category { get; init; } = AlertCategory.General;
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public bool Acknowledged { get; set; }

    public string TimeText => Timestamp.ToString("HH:mm:ss");
    public string SeverityText => Severity.ToString().ToUpperInvariant();

    public Brush SeverityBrush => HomeRms.Controller.Themes.Palette.Fg(Severity switch
    {
        AlertSeverity.Critical => HomeRms.Controller.Themes.PaletteTone.Danger,
        AlertSeverity.Warning => HomeRms.Controller.Themes.PaletteTone.Warning,
        _ => HomeRms.Controller.Themes.PaletteTone.Info,
    });
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
    public event Action<AlertItem>? AlertRaised;

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
        catch (Exception ex)
        {
            LogBus.Publish(LogBus.Alerts, "Settings", $"Alert thresholds could not be read ({ex.Message}); using defaults.");
        }
    }

    public void SaveThresholds()
    {
        try
        {
            HomeRms.Shared.AtomicFile.WriteAllText(ThresholdsPath, JsonSerializer.Serialize(Thresholds, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            LogBus.Publish(LogBus.Alerts, "Settings", $"Could not save alert thresholds: {ex.Message}");
        }
    }

    public bool IsEnabled(AlertCategory category) => category switch
    {
        AlertCategory.EndpointOffline => Thresholds.AlertOnOffline,
        AlertCategory.HighCpu => Thresholds.AlertOnHighCpu,
        AlertCategory.HighMemory => Thresholds.AlertOnHighRam,
        AlertCategory.LowDisk => Thresholds.AlertOnLowDisk,
        AlertCategory.Thermal => Thresholds.AlertOnThermal,
        AlertCategory.BlockedSite => Thresholds.AlertOnBlockedSite,
        AlertCategory.AgentWarning => Thresholds.AlertOnAgentWarning,
        AlertCategory.ControllerUpdate => Thresholds.AlertOnControllerUpdate,
        _ => true,
    };

    public void EvaluateSample(string endpointName, string host, bool isOnline, double cpuPercent, double ramPercent, double? minDiskFreePercent = null, double? maxTempC = null)
    {
        if (_wasOnline.TryGetValue(host, out var prevOnline) && prevOnline && !isOnline && Thresholds.AlertOnOffline)
        {
            Raise(endpointName, host, AlertSeverity.Critical, "Endpoint Offline",
                $"{endpointName} ({host}) stopped responding to health probes.", cooldownMinutes: 5, category: AlertCategory.EndpointOffline);
        }
        _wasOnline[host] = isOnline;

        if (!isOnline) return;

        if (Thresholds.AlertOnHighCpu && cpuPercent >= Thresholds.CpuThresholdPercent)
        {
            Raise(endpointName, host, AlertSeverity.Warning, "High CPU Utilization",
                $"CPU load at {cpuPercent:0}% (threshold {Thresholds.CpuThresholdPercent:0}%).", cooldownMinutes: 3, category: AlertCategory.HighCpu);
        }

        if (Thresholds.AlertOnHighRam && ramPercent >= Thresholds.RamThresholdPercent)
        {
            Raise(endpointName, host, AlertSeverity.Warning, "High Memory Utilization",
                $"RAM usage at {ramPercent:0}% (threshold {Thresholds.RamThresholdPercent:0}%).", cooldownMinutes: 3, category: AlertCategory.HighMemory);
        }

        if (Thresholds.AlertOnLowDisk && minDiskFreePercent is < 100 && minDiskFreePercent.Value < Thresholds.DiskFreeMinPercent)
        {
            Raise(endpointName, host, AlertSeverity.Critical, "Low Disk Space",
                $"Free disk space dropped to {minDiskFreePercent.Value:0.0}% (min {Thresholds.DiskFreeMinPercent:0}%).", cooldownMinutes: 15, category: AlertCategory.LowDisk);
        }

        if (Thresholds.AlertOnThermal && maxTempC is > 0 && maxTempC.Value >= Thresholds.TempThresholdC)
        {
            Raise(endpointName, host, AlertSeverity.Critical, "Thermal Warning",
                $"Hardware temperature reached {maxTempC.Value:0}°C (limit {Thresholds.TempThresholdC:0}°C).", cooldownMinutes: 5, category: AlertCategory.Thermal);
        }
    }

    public void Raise(string endpointName, string host, AlertSeverity severity, string title, string detail, int cooldownMinutes = 2, AlertCategory category = AlertCategory.General)
    {
        if (!IsEnabled(category)) return;
        var key = $"{host}|{title}";
        var now = DateTimeOffset.Now;
        if (cooldownMinutes > 0)
        {
            if (_cooldowns.TryGetValue(key, out var until) && now < until)
                return;
            _cooldowns[key] = now.AddMinutes(cooldownMinutes);
        }

        var item = new AlertItem
        {
            EndpointName = endpointName,
            Host = host,
            Severity = severity,
            Category = category,
            Title = title,
            Detail = detail,
            Timestamp = now,
        };

        LogBus.Publish(LogBus.Alerts, endpointName, $"{severity.ToString().ToUpperInvariant()} {title} — {detail}",
            severity == AlertSeverity.Critical ? HomeRms.Controller.Themes.PaletteTone.Danger
            : severity == AlertSeverity.Warning ? HomeRms.Controller.Themes.PaletteTone.Warning
            : HomeRms.Controller.Themes.PaletteTone.Info);

        void Insert()
        {
            Alerts.Insert(0, item);
            while (Alerts.Count > 200) Alerts.RemoveAt(Alerts.Count - 1);
            AlertsChanged?.Invoke();
            AlertRaised?.Invoke(item);
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
