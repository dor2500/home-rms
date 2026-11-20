using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Controller.Views;

namespace HomeRms.Controller;

/// <summary>
/// v2.1 feature views (file manager, services &amp; tasks, script library, live telemetry, power bar).
/// Each view is a self-contained UserControl under Views\; this partial only wires them into the
/// shell (host ContentControls + nav RadioButtons declared in MainWindow.xaml).
/// </summary>
public partial class MainWindow
{
    private AgentConnectionPool _pool = null!;
    private TelemetryHub _telemetry = null!;
    private ScriptExecutor _scriptExecutor = null!;
    private FeatureContext _features = null!;
    private DispatcherTimer? _telemetrySyncTimer;

    private readonly TelemetryOverviewView _telemetryView = new();
    private readonly FileManagerView _fileManagerView = new();
    private readonly ServicesTasksView _servicesView = new();
    private readonly ScriptLibraryView _scriptLibraryView = new();
    private readonly EndpointActionBar _fleetActionBar = new();

    private void InitializeFeatureViews()
    {
        _pool = new AgentConnectionPool { TwoFactorPrompt = PromptAgent2FaAsync };
        _telemetry = new TelemetryHub(_pool, _alerts, Dispatcher);
        _telemetry.SampleReceived += OnTelemetrySample;
        _scriptExecutor = new ScriptExecutor(_pool, _agentless, Dispatcher);
        _features = new FeatureContext
        {
            Machines = _machines,
            Pool = _pool,
            Alerts = _alerts,
            Agentless = _agentless,
            Telemetry = _telemetry,
            Scripts = _scriptExecutor,
            Log = AddActivity,
            SaveMachines = () => MachineStore.Save(_machines),
            FocusedEndpoint = () => _connectedProfile ?? SelectedMachine,
            SelectedEndpoints = SelectedFleetEndpoints,
        };

        _telemetryView.Initialize(_features);
        _fileManagerView.Initialize(_features);
        _servicesView.Initialize(_features);
        _scriptLibraryView.Initialize(_features);
        _fleetActionBar.Context = _features;
        _fleetActionBar.TargetsProvider = SelectedFleetEndpoints;

        TelemetryHost.Content = _telemetryView;
        FilesHost.Content = _fileManagerView;
        ServicesHost.Content = _servicesView;
        ScriptsHost.Content = _scriptLibraryView;
        FleetPowerHost.Content = _fleetActionBar;

        // Telemetry streams follow the fleet's online state (probes run on their own schedule).
        _telemetrySyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _telemetrySyncTimer.Tick += (_, _) => _telemetry.Sync(_machines);
        _telemetrySyncTimer.Start();
        _machines.CollectionChanged += OnMachinesChangedForFeatures;

        Closed += (_, _) =>
        {
            _telemetrySyncTimer?.Stop();
            _telemetry.Dispose();
            _pool.Dispose();
        };
    }

    private void OnMachinesChangedForFeatures(object? sender, NotifyCollectionChangedEventArgs e) => _telemetry.Sync(_machines);

    private void OnTelemetrySample()
    {
        foreach (var et in _telemetry.Endpoints.Where(t => t.IsLive))
        {
            et.Profile.LastSeenUtc = et.LastSampleUtc;
            et.Profile.UptimeSeconds = et.UptimeSeconds;
        }
        UpdateOverviewMetrics();
    }

    /// <summary>All endpoints selected in the Endpoints card grid (multi-select).</summary>
    private IReadOnlyList<MachineProfile> SelectedFleetEndpoints()
    {
        var list = MachinesList.SelectedItems.OfType<MachineProfile>().ToList();
        if (list.Count == 0 && SelectedMachine is { } m) list.Add(m);
        return list;
    }

    /// <summary>Shows / hides the feature views; called at the end of <c>OnNavTabChanged</c>.</summary>
    private void UpdateFeatureViewVisibility()
    {
        if (FilesHost is null || ServicesHost is null || ScriptsHost is null) return;
        Show(FilesHost, NavFiles.IsChecked == true, _fileManagerView.OnShown);
        Show(ServicesHost, NavServices.IsChecked == true, _servicesView.OnShown);
        Show(ScriptsHost, NavScripts.IsChecked == true, _scriptLibraryView.OnShown);

        static void Show(ContentControl host, bool visible, Action onShown)
        {
            var was = host.Visibility == Visibility.Visible;
            host.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && !was) onShown();
        }
    }

    private void UpdateFeatureNavLabels()
    {
        var vis = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        NavLabelFiles.Visibility = vis;
        NavLabelServices.Visibility = vis;
        NavLabelScripts.Visibility = vis;
    }

    /// <summary>Interactive 2FA pairing prompt for the feature views (reuses the connect overlay).</summary>
    private Task<string?> PromptAgent2FaAsync(string target) =>
        Dispatcher.InvokeAsync(async () =>
        {
            if (IsConsoleLocked) return null; // never prompt over the lock screen
            _agent2FaTcs?.TrySetResult(null); // a superseded prompt must not hang its caller
            _agent2FaTcs = new TaskCompletionSource<string?>();
            Agent2FaCodeBox.Text = "";
            AgentPairPromptText.Text = $"Enter the 6-digit 2FA code shown on {target} to pair and pin this Controller:";
            AgentPair2FaOverlay.Visibility = Visibility.Visible;
            Agent2FaCodeBox.Focus();
            return await _agent2FaTcs.Task;
        }).Task.Unwrap();

    /// <summary>Mass Run: the broadcast command runs through the Script Library executor (pairing-aware, max 4 in parallel).</summary>
    private async Task MassRunAsync(string command)
    {
        var targets = _machines.Where(m => (m.IsOnline || m.IsConnected) && m.IsWindows).ToList();
        if (targets.Count == 0)
        {
            AddActivity("MASS RUN", "No online Windows endpoints found. Run Scan LAN first.");
            return;
        }

        AddActivity("MASS RUN", $"Executing on {targets.Count} online endpoint(s): {command}");
        var (runs, completion) = _scriptExecutor.Start(targets, ScriptShell.PowerShell, command, "Mass Run",
            TimeSpan.FromMinutes(3), CancellationToken.None);
        await completion;
        foreach (var r in runs)
        {
            if (r.State == ScriptRunState.Succeeded)
            {
                var last = r.Output.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "OK";
                AddActivity("MASS RUN", $"[{r.EndpointName}] -> {last}");
            }
            else
            {
                AddActivity("ALERT", $"[{r.EndpointName}] Mass Run {r.StateText.ToLowerInvariant()}: {r.Summary}");
            }
        }
    }
}
