using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Shared;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Views;

public sealed class ServiceRow(Dictionary<string, string> raw)
{
    public Dictionary<string, string> Raw { get; } = raw;
    public string Name => Raw.GetValueOrDefault("Name", "");
    public string DisplayName => Raw.GetValueOrDefault("DisplayName", "");
    public string State => Raw.GetValueOrDefault("State", "");
    public string StartMode => Raw.GetValueOrDefault("StartMode", "");
    public string StartName => Raw.GetValueOrDefault("StartName", "");
}

public sealed class TaskRow(Dictionary<string, string> raw)
{
    public Dictionary<string, string> Raw { get; } = raw;
    public string TaskPath => Raw.GetValueOrDefault("TaskPath", "");
    public string TaskName => Raw.GetValueOrDefault("TaskName", "");
    public string State => Raw.GetValueOrDefault("State", "");
    public string FullPath => SystemCatalog.TargetFromRow("tasks", Raw);
}

/// <summary>Services and scheduled tasks of one agent, via the allowlisted SystemOp RPC.</summary>
public partial class ServicesTasksView : UserControl
{
    private readonly ObservableCollection<ServiceRow> _services = new();
    private readonly ObservableCollection<TaskRow> _tasks = new();
    private readonly ICollectionView _servicesView;
    private readonly ICollectionView _tasksView;
    private FeatureContext? _ctx;
    private MachineProfile? _loadedFor;
    private bool _tasksLoaded;
    private bool _busy;

    public ServicesTasksView()
    {
        InitializeComponent();
        // The views keep their SortDescriptions (set by DataGrid header clicks) and filter while the
        // underlying collections are refilled on refresh.
        _servicesView = CollectionViewSource.GetDefaultView(_services);
        _servicesView.SortDescriptions.Add(new SortDescription(nameof(ServiceRow.Name), ListSortDirection.Ascending));
        _servicesView.Filter = o => o is ServiceRow r && MatchService(r);
        ServicesGrid.ItemsSource = _servicesView;

        _tasksView = CollectionViewSource.GetDefaultView(_tasks);
        _tasksView.SortDescriptions.Add(new SortDescription(nameof(TaskRow.TaskPath), ListSortDirection.Ascending));
        _tasksView.SortDescriptions.Add(new SortDescription(nameof(TaskRow.TaskName), ListSortDirection.Ascending));
        _tasksView.Filter = o => o is TaskRow r && MatchTask(r);
        TasksGrid.ItemsSource = _tasksView;
    }

    public void Initialize(FeatureContext ctx)
    {
        _ctx = ctx;
        ActionBar.Context = ctx;
        ActionBar.TargetsProvider = () => EndpointCombo.SelectedItem is MachineProfile m ? [m] : [];
        RefreshEndpointList();
    }

    public async void OnShown()
    {
        RefreshEndpointList();
        if (_loadedFor is null && EndpointCombo.SelectedItem is MachineProfile m && (m.IsOnline || m.IsConnected))
            await LoadAsync();
    }

    private void RefreshEndpointList()
    {
        if (_ctx is null) return;
        var current = EndpointCombo.SelectedItem as MachineProfile;
        var list = _ctx.AgentEndpoints.ToList();
        EndpointCombo.ItemsSource = list;
        var focused = _ctx.FocusedEndpoint();
        EndpointCombo.SelectedItem =
            current is not null && list.Contains(current) ? current :
            focused is not null && list.Contains(focused) ? focused :
            list.FirstOrDefault(m => m.IsConnected) ?? list.FirstOrDefault(m => m.IsOnline) ?? list.FirstOrDefault();
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusLine.Text = text;
        StatusLine.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "MutedBrush");
    }

    private async Task<SystemOpResult?> RunOpAsync(string area, string action, string target = "", string extra = "")
    {
        if (_ctx is null || EndpointCombo.SelectedItem is not MachineProfile m) return null;
        try
        {
            var res = await _ctx.Pool.CallAsync(m, interactive: true, c => c.SystemOpAsync(
                new SystemOpRequest { Area = area, Action = action, Target = target, Extra = extra },
                deadline: DateTime.UtcNow.AddMinutes(2)).ResponseAsync);
            if (!res.IsElevated && action != "list")
                SetStatus("Note: the agent is not running elevated; some operations may be denied.");
            return res;
        }
        catch (Exception ex)
        {
            SetStatus($"[ERR] {area}/{action}: {AgentErrors.Describe(ex)}", error: true);
            return null;
        }
    }

    private async void OnLoadClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        _tasksLoaded = false;
        _loadedFor = EndpointCombo.SelectedItem as MachineProfile;
        if (_loadedFor is null) return;
        SubtitleText.Text = $"{_loadedFor.DisplayTitle} ({_loadedFor.Address})";
        await RefreshServicesAsync();
        if (Tabs.SelectedIndex == 1) await RefreshTasksAsync();
    }

    private async void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        if (Tabs.SelectedIndex == 1 && !_tasksLoaded && _loadedFor is not null) await RefreshTasksAsync();
    }

    // ---------------- Services ----------------
    private bool MatchService(ServiceRow r)
    {
        var q = ServiceFilterBox?.Text?.Trim() ?? "";
        return q.Length == 0 || r.Raw.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void OnServiceFilterChanged(object sender, TextChangedEventArgs e) => _servicesView.Refresh();

    private async void OnRefreshServicesClick(object sender, RoutedEventArgs e) => await RefreshServicesAsync();

    private async Task RefreshServicesAsync(bool preserveStatus = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!preserveStatus) SetStatus("Loading services…");
            var res = await RunOpAsync("services", "list");
            if (res is null) return;
            if (!res.Success)
            {
                if (!preserveStatus) SetStatus("[ERR] " + res.Message, error: true);
                return;
            }

            var keep = ServicesGrid.SelectedItems.Cast<ServiceRow>().Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var table = SystemCatalog.ParseTable(res.JsonData);
            _services.Clear();
            foreach (var row in table.Rows) _services.Add(new ServiceRow(row));
            foreach (var r in _services.Where(r => keep.Contains(r.Name))) ServicesGrid.SelectedItems.Add(r);
            if (!preserveStatus)
                SetStatus($"{_services.Count} services · {_services.Count(s => s.State == "Running")} running · refreshed {DateTime.Now:HH:mm:ss}" +
                          (res.IsElevated ? "" : " · agent NOT elevated"));
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnServiceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ServiceDetails.Text = ServicesGrid.SelectedItem is ServiceRow r
            ? string.Join("\n", r.Raw.Select(kv => $"{kv.Key}:\n  {kv.Value}"))
            : "Select a service.";
    }

    private async void OnServiceActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        var rows = ServicesGrid.SelectedItems.Cast<ServiceRow>().ToList();
        if (rows.Count == 0) { SetStatus("Select one or more services first.", error: true); return; }
        if (action is "stop" or "restart" && !Confirm($"{Cap(action)} {rows.Count} service(s)?\n\n{List(rows.Select(r => r.Name))}"))
            return;
        await RunBatchAsync("services", action, rows.Select(r => r.Name).ToList(), "");
        await RefreshServicesAsync(preserveStatus: true);
    }

    private async void OnSetStartupClick(object sender, RoutedEventArgs e)
    {
        var rows = ServicesGrid.SelectedItems.Cast<ServiceRow>().ToList();
        if (rows.Count == 0) { SetStatus("Select one or more services first.", error: true); return; }
        var type = (StartupTypeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "Manual";
        if (!Confirm($"Set the startup type of {rows.Count} service(s) to {type}?\n\n{List(rows.Select(r => r.Name))}")) return;
        await RunBatchAsync("services", "set-startup", rows.Select(r => r.Name).ToList(), type);
        await RefreshServicesAsync(preserveStatus: true);
    }

    // ---------------- Tasks ----------------
    private bool MatchTask(TaskRow r)
    {
        if (HideMicrosoftTasksCheck?.IsChecked == true && r.TaskPath.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase))
            return false;
        var q = TaskFilterBox?.Text?.Trim() ?? "";
        return q.Length == 0 || r.Raw.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void OnTaskFilterChanged(object sender, RoutedEventArgs e) => _tasksView?.Refresh();

    private async void OnRefreshTasksClick(object sender, RoutedEventArgs e) => await RefreshTasksAsync();

    private async Task RefreshTasksAsync(bool preserveStatus = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!preserveStatus) SetStatus("Loading scheduled tasks…");
            var res = await RunOpAsync("tasks", "list");
            if (res is null) return;
            if (!res.Success)
            {
                if (!preserveStatus) SetStatus("[ERR] " + res.Message, error: true);
                return;
            }

            var keep = TasksGrid.SelectedItems.Cast<TaskRow>().Select(r => r.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var table = SystemCatalog.ParseTable(res.JsonData);
            _tasks.Clear();
            foreach (var row in table.Rows) _tasks.Add(new TaskRow(row));
            foreach (var r in _tasks.Where(r => keep.Contains(r.FullPath))) TasksGrid.SelectedItems.Add(r);
            _tasksLoaded = true;
            if (!preserveStatus)
                SetStatus($"{_tasks.Count} scheduled tasks · {_tasks.Count(t => t.State == "Disabled")} disabled · refreshed {DateTime.Now:HH:mm:ss}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TaskDetails.Text = TasksGrid.SelectedItem is TaskRow r
            ? $"Full path:\n  {r.FullPath}\n" + string.Join("\n", r.Raw.Select(kv => $"{kv.Key}:\n  {kv.Value}"))
            : "Select a task.";
    }

    private async void OnTaskActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        var rows = TasksGrid.SelectedItems.Cast<TaskRow>().ToList();
        if (rows.Count == 0) { SetStatus("Select one or more tasks first.", error: true); return; }
        if (action == "disable" && !Confirm($"Disable {rows.Count} scheduled task(s)?\n\n{List(rows.Select(r => r.FullPath))}"))
            return;
        await RunBatchAsync("tasks", action, rows.Select(r => r.FullPath).ToList(), "");
        await RefreshTasksAsync(preserveStatus: true);
    }

    // ---------------- Helpers ----------------
    private async Task RunBatchAsync(string area, string action, IReadOnlyList<string> targets, string extra)
    {
        var ok = 0;
        var transportFailed = false;
        var errors = new List<string>();
        foreach (var t in targets)
        {
            SetStatus($"{area}/{action}: {t}…");
            var res = await RunOpAsync(area, action, t, extra);
            if (res is null)
            {
                transportFailed = true;
                break; // transport / permission error already shown
            }
            if (res.Success) ok++;
            else errors.Add($"{t}: {res.Message}");
        }
        if (ok > 0 || !transportFailed)
        {
            var who = (EndpointCombo.SelectedItem as MachineProfile)?.DisplayTitle ?? "endpoint";
            _ctx?.Log("SYSTEM", $"{area}/{action} on {who}: {ok}/{targets.Count} OK");
        }
        if (transportFailed) return;
        if (errors.Count == 0)
            SetStatus($"[OK] {area}/{action} succeeded for {ok} item(s).");
        else
            SetStatus($"[ERR] {errors.Count} failed — {string.Join("; ", errors.Take(3))}", error: true);
    }

    private bool Confirm(string text) =>
        MessageBox.Show(Window.GetWindow(this)!, text + $"\n\nEndpoint: {(EndpointCombo.SelectedItem as MachineProfile)?.DisplayTitle}",
            "Home RMS — Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private static string List(IEnumerable<string> items)
    {
        var l = items.ToList();
        return string.Join("\n", l.Take(10).Select(i => "  • " + i)) + (l.Count > 10 ? $"\n  … and {l.Count - 10} more" : "");
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
