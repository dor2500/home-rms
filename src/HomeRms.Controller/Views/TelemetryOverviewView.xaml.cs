using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views;

/// <summary>Overview tiles: per-endpoint live sparklines, fleet totals, alert thresholds and active alerts.</summary>
public partial class TelemetryOverviewView : UserControl
{
    private FeatureContext? _ctx;

    public TelemetryOverviewView()
    {
        InitializeComponent();
    }

    public void Initialize(FeatureContext ctx)
    {
        _ctx = ctx;
        TilesList.ItemsSource = ctx.Telemetry.Endpoints;
        AlertsList.ItemsSource = ctx.Alerts.Alerts;
        ctx.Telemetry.Endpoints.CollectionChanged += OnTilesChanged;
        ctx.Telemetry.SampleReceived += UpdateTotals;
        LoadThresholds();
        OnTilesChanged(null, null);
    }

    private void OnTilesChanged(object? sender, NotifyCollectionChangedEventArgs? e)
    {
        EmptyText.Visibility = _ctx?.Telemetry.Endpoints.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateTotals();
    }

    private void LoadThresholds()
    {
        if (_ctx is null) return;
        var t = _ctx.Alerts.Thresholds;
        CpuBox.Text = t.CpuThresholdPercent.ToString("0");
        RamBox.Text = t.RamThresholdPercent.ToString("0");
        DiskBox.Text = t.DiskFreeMinPercent.ToString("0");
        OfflineCheck.IsChecked = t.AlertOnOffline;
    }

    private void OnSaveThresholdsClick(object sender, RoutedEventArgs e)
    {
        if (_ctx is null) return;
        var t = _ctx.Alerts.Thresholds;
        if (double.TryParse(CpuBox.Text, out var cpu)) t.CpuThresholdPercent = Math.Clamp(cpu, 10, 100);
        if (double.TryParse(RamBox.Text, out var ram)) t.RamThresholdPercent = Math.Clamp(ram, 10, 100);
        if (double.TryParse(DiskBox.Text, out var dsk)) t.DiskFreeMinPercent = Math.Clamp(dsk, 1, 90);
        t.AlertOnOffline = OfflineCheck.IsChecked == true;
        _ctx.Alerts.SaveThresholds();
        foreach (var et in _ctx.Telemetry.Endpoints)
            et.RefreshHot(t);
        LoadThresholds();
        _ctx.Log("ALERT", $"Alert thresholds saved: CPU ≥ {t.CpuThresholdPercent:0}%, RAM ≥ {t.RamThresholdPercent:0}%, disk free < {t.DiskFreeMinPercent:0}%, offline {(t.AlertOnOffline ? "on" : "off")}.");
    }

    private void UpdateTotals()
    {
        if (_ctx is null) return;
        var live = _ctx.Telemetry.Endpoints.Where(e => e.IsLive && e.Latest is not null).Select(e => e.Latest!.Value).ToList();
        if (live.Count == 0)
        {
            FleetTotalsText.Text = _ctx.Telemetry.Endpoints.Count == 0
                ? "No live agent telemetry yet."
                : $"{_ctx.Telemetry.Endpoints.Count} agent endpoint(s) tracked · none streaming";
            return;
        }
        FleetTotalsText.Text =
            $"{live.Count}/{_ctx.Telemetry.Endpoints.Count} live · avg CPU {live.Average(p => p.Cpu):0}% · avg RAM {live.Average(p => p.Ram):0}% · " +
            $"fullest disk {live.Max(p => p.DiskMax):0}% · net ↓ {EndpointTelemetry.FormatRate(live.Sum(p => p.RxBps))} ↑ {EndpointTelemetry.FormatRate(live.Sum(p => p.TxBps))}";
    }
}
