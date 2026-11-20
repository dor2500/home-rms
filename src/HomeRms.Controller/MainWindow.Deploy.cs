using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

namespace HomeRms.Controller;

/// <summary>A Windows endpoint that can be ticked as a deployment target in the Deploy panel.</summary>
public sealed class DeployTarget : INotifyPropertyChanged
{
    private bool _isChecked;

    public required MachineProfile Profile { get; init; }
    public required string Detail { get; init; }
    public string Name => Profile.DisplayTitle;
    public string Address => Profile.Address;

    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One local installer / script in the file-deploy list.</summary>
public sealed class DeployFileItem : INotifyPropertyChanged
{
    private DeployItemStatus _status = DeployItemStatus.Pending;
    private int _percent = -1;
    private string _detail = "";

    public required string FullPath { get; init; }
    public string FileName => System.IO.Path.GetFileName(FullPath);

    public DeployItemStatus DeployStatus
    {
        get => _status;
        set { _status = value; Notify(); Notify(nameof(StatusLabel)); Notify(nameof(ProgressVisibility)); Notify(nameof(IsIndeterminate)); }
    }

    public int Percent
    {
        get => _percent;
        set { _percent = value; Notify(); Notify(nameof(ProgressValue)); Notify(nameof(IsIndeterminate)); Notify(nameof(StatusLabel)); }
    }

    public string Detail { get => _detail; set { _detail = value; Notify(); } }

    public double ProgressValue => Math.Clamp(_percent, 0, 100);
    public bool IsBusy => _status is DeployItemStatus.Downloading or DeployItemStatus.Installing;
    public bool IsIndeterminate => IsBusy && _percent < 0;
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public string StatusLabel => _status switch
    {
        DeployItemStatus.Downloading => $"Uploading {Math.Max(0, _percent)}%",
        DeployItemStatus.Installing => "Running",
        DeployItemStatus.Success => "Done",
        DeployItemStatus.Failed => "Failed",
        DeployItemStatus.Cancelled => "Cancelled",
        _ => "Pending",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public partial class MainWindow
{
    private const int WingetPageSize = 50;
    private const string BuiltInSourceLabel = "built-in curated list";

    private readonly WingetCatalogService _winget = new();
    private readonly WingetSourceIndex _wingetIndex = new();
    private readonly WingetIconCache _wingetIcons = new();
    private readonly ObservableCollection<WingetAppItem> _filteredWingetApps = [];
    private readonly ObservableCollection<WingetAppItem> _wingetQueue = [];
    private readonly ObservableCollection<DeployTarget> _deployTargets = [];
    private readonly ObservableCollection<DeployFileItem> _deployFiles = [];
    private readonly DispatcherTimer _wingetSearchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private CancellationTokenSource? _wingetSearchCts;
    private CancellationTokenSource? _wingetDeployCts;
    private int _wingetSearchGeneration;
    private int _wingetPageOffset;
    private bool _wingetIndexLoadStarted;
    private bool _deployInitialized;

    private sealed record WingetTarget(string Label, MachineProfile? Profile, bool Agentless);
    private sealed record WingetOutcome(string Target, DeployItemStatus Status, string Detail);

    private void InitializeDeploy()
    {
        WingetCatalogList.ItemsSource = _filteredWingetApps;
        WingetQueueList.ItemsSource = _wingetQueue;
        DeployTargetList.ItemsSource = _deployTargets;
        DeployFileList.ItemsSource = _deployFiles;
        WingetPackCombo.DisplayMemberPath = nameof(WingetPack.DisplayName);
        ReloadWingetPackCombo(null);
        WingetCategoryCombo.ItemsSource = WingetCategories.Names;
        WingetCategoryCombo.SelectedIndex = 0;

        _wingetSearchDebounce.Tick += (_, _) =>
        {
            _wingetSearchDebounce.Stop();
            _ = RunWingetSearchAsync(append: false);
        };
        _wingetQueue.CollectionChanged += (_, _) => UpdateWingetSelectedCount();

        if (_winget.PackLoadError is { } packErr)
            DeployLog("packs", null, packErr);

        _deployInitialized = true;
        UpdateWingetSourceStatus();
        _ = RunWingetSearchAsync(append: false);
    }

    private void OnDeployPanelVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_deployInitialized || e.NewValue is not true) return;
        RefreshDeployTargets();
        if (!_wingetIndexLoadStarted)
        {
            _wingetIndexLoadStarted = true;
            _ = EnsureWingetIndexAsync(force: false);
        }
    }

    // ---------------- Catalog source (official index) ----------------
    private async Task EnsureWingetIndexAsync(bool force)
    {
        WingetSourceStatusText.Text = force
            ? "Checking cdn.winget.microsoft.com for a newer winget index..."
            : "Loading the official winget index (downloads about 4 MB at most once a day)...";
        DeployStatusBadge.Text = "INDEX...";
        try
        {
            await Task.Run(() => _wingetIndex.EnsureFreshAsync(force));
        }
        catch (Exception ex)
        {
            DeployLog("catalog", null, $"Official winget index refresh failed: {ex.Message}" +
                (_wingetIndex.IsAvailable ? " (keeping the cached copy)" : " (falling back to api.winget.run / built-in list)"));
        }

        RefreshBuiltInVersionsFromIndex();
        UpdateWingetSourceStatus();
        DeployStatusBadge.Text = _wingetDeployCts is null ? "READY" : "DEPLOYING...";
        await RunWingetSearchAsync(append: false);
    }

    private void RefreshBuiltInVersionsFromIndex()
    {
        if (!_wingetIndex.IsAvailable) return;
        try
        {
            var found = _wingetIndex.Lookup(_winget.BuiltIn.Select(a => a.Id));
            foreach (var app in _winget.BuiltIn)
                if (found.TryGetValue(app.Id, out var p)) app.Version = p.Version;
        }
        catch (Exception ex)
        {
            DeployLog("catalog", null, $"Index lookup failed: {ex.Message}");
        }
    }

    private void UpdateWingetSourceStatus()
    {
        var s = _wingetIndex.Status;
        string text;
        if (s.Available)
        {
            var updated = s.UpdatedUtc is { } u ? u.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "unknown date";
            text = $"Source: official winget index (cdn.winget.microsoft.com), updated {updated}, {s.PackageCount:N0} packages";
            if (s.Error is not null) text += $"  ·  last refresh failed: {s.Error} (using the cached copy)";
        }
        else if (s.Error is not null)
        {
            text = $"Official winget index unavailable: {s.Error}  ·  search falls back to api.winget.run (community mirror, frozen since 2023), then the built-in list.";
        }
        else
        {
            text = "Official winget index not downloaded yet  ·  showing the built-in list.";
        }
        WingetSourceStatusText.Text = text;
    }

    private void OnRefreshWingetIndexClick(object sender, RoutedEventArgs e)
    {
        _wingetIndexLoadStarted = true;
        _ = EnsureWingetIndexAsync(force: true);
    }

    // ---------------- Search ----------------
    private void OnWingetSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_deployInitialized) return;
        _wingetSearchDebounce.Stop();
        _wingetSearchDebounce.Start();
    }

    private void OnWingetCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_deployInitialized) return;
        _wingetSearchDebounce.Stop();
        _ = RunWingetSearchAsync(append: false);
    }

    private void OnWingetLoadMoreClick(object sender, RoutedEventArgs e) => _ = RunWingetSearchAsync(append: true);

    private async Task RunWingetSearchAsync(bool append)
    {
        var query = WingetSearchBox.Text.Trim();
        var category = WingetCategoryCombo.SelectedItem as string ?? WingetCategories.All;
        var gen = ++_wingetSearchGeneration;
        _wingetSearchCts?.Cancel();
        var cts = _wingetSearchCts = new CancellationTokenSource();
        var offset = append ? _wingetPageOffset : 0;

        List<WingetAppItem>? page = null;
        var total = 0;
        var source = "";
        var canPage = false;
        var problems = new List<string>();

        if (query.Length == 0 && category == WingetCategories.All)
        {
            page = _winget.BuiltIn;
            total = page.Count;
            source = BuiltInSourceLabel + (_wingetIndex.IsAvailable ? $"; type to search all {_wingetIndex.Status.PackageCount:N0} packages" : "");
        }
        else
        {
            if (_wingetIndex.IsAvailable)
            {
                try
                {
                    var tags = category == WingetCategories.All ? null : WingetCategories.TagsFor(category);
                    var (rows, t) = await Task.Run(() => _wingetIndex.Search(query, tags, offset, WingetPageSize), cts.Token);
                    if (gen != _wingetSearchGeneration) return;
                    page = rows.Select(_winget.FromIndex).ToList();
                    total = t;
                    source = "official winget index";
                    canPage = true;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    problems.Add($"official index search failed: {ex.Message}");
                }
            }

            if (page is null && query.Length > 0 && !append)
            {
                try
                {
                    var found = await _winget.SearchWingetRunAsync(query, cts.Token);
                    if (gen != _wingetSearchGeneration) return;
                    page = category == WingetCategories.All ? found : found.Where(a => a.Category == category).ToList();
                    total = page.Count;
                    source = "api.winget.run (community mirror, data frozen since 2023)";
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    problems.Add($"api.winget.run failed: {(ex is TaskCanceledException ? "timed out" : ex.Message)}");
                }
            }

            if (page is null)
            {
                page = _winget.BuiltIn.Where(a =>
                        (category == WingetCategories.All || a.Category == category) &&
                        (query.Length == 0 ||
                         a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         a.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         a.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                total = page.Count;
                source = BuiltInSourceLabel;
            }
        }

        if (gen != _wingetSearchGeneration) return;

        if (!append)
        {
            _filteredWingetApps.Clear();
            WingetCatalogScroll.ScrollToTop();
        }
        foreach (var item in page)
        {
            if (_filteredWingetApps.Contains(item)) continue;
            _filteredWingetApps.Add(item);
            _wingetIcons.Attach(item);
        }
        _wingetPageOffset = offset + page.Count;
        WingetLoadMoreBtn.Visibility = canPage && _wingetPageOffset < total ? Visibility.Visible : Visibility.Collapsed;

        var summary = $"Showing {_filteredWingetApps.Count:N0} of {total:N0}  ·  source: {source}";
        if (problems.Count > 0)
        {
            summary += "  ·  " + string.Join("; ", problems);
            foreach (var p in problems) DeployLog("catalog", null, p);
        }
        WingetResultCountText.Text = summary;
        WingetResultCountText.ToolTip = summary;

        // Keep only what is visible, queued, busy or built-in (search results never accumulate).
        if (!append) _winget.Prune(_filteredWingetApps.Concat(_wingetQueue));
    }

    // ---------------- Queue ----------------
    private void UpdateWingetSelectedCount()
    {
        if (WingetDeploySelectedBtn is null) return;
        WingetDeploySelectedBtn.Content = $"Deploy Queue ({_wingetQueue.Count})";
        WingetQueueCountText.Text = $"DEPLOYMENT QUEUE ({_wingetQueue.Count})";
    }

    private void SetQueued(WingetAppItem item, bool queued)
    {
        if (queued)
        {
            item.IsSelected = true;
            if (!_wingetQueue.Contains(item))
            {
                _wingetQueue.Add(item);
                _wingetIcons.Attach(item);
            }
            return;
        }

        if (item.IsBusy && _wingetDeployCts is not null)
        {
            item.IsSelected = true;
            StatusText.Text = $"{item.Name} is being deployed; cancel the deployment first.";
            return;
        }
        item.IsSelected = false;
        _wingetQueue.Remove(item);
    }

    private void OnWingetCardSelectionChanged(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WingetAppItem item)
            SetQueued(item, item.IsSelected);
    }

    private void OnRemoveWingetQueueItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WingetAppItem item)
            SetQueued(item, false);
    }

    private void OnClearWingetQueueClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in _wingetQueue.ToList())
            SetQueued(item, false);
    }

    /// <summary>Adds validated IDs to the queue, resolving names/versions from the index when possible.</summary>
    private (int Added, List<string> NotInIndex) AddIdsToQueue(IEnumerable<string> ids, PackImportResult? info)
    {
        var list = ids.Where(WingetRules.IsValidPackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Dictionary<string, IndexPackage> found = new(StringComparer.OrdinalIgnoreCase);
        if (_wingetIndex.IsAvailable)
        {
            try { found = _wingetIndex.Lookup(list); }
            catch (Exception ex) { DeployLog("catalog", null, $"Index lookup failed: {ex.Message}"); }
        }

        var added = 0;
        var notInIndex = new List<string>();
        foreach (var id in list)
        {
            ImportedPackageInfo? meta = null;
            info?.Info.TryGetValue(id, out meta);

            WingetAppItem item;
            if (found.TryGetValue(id, out var pkg))
            {
                item = _winget.FromIndex(pkg);
                if (!item.IsBuiltIn && !string.IsNullOrWhiteSpace(meta?.Description)) item.Description = meta.Description!;
            }
            else
            {
                if (_wingetIndex.IsAvailable && _winget.Find(id) is not { IsBuiltIn: true }) notInIndex.Add(id);
                item = _winget.Find(id) ?? _winget.GetOrCreate(id, () => new WingetAppItem
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(meta?.Name) ? id : meta.Name!,
                    Publisher = string.IsNullOrWhiteSpace(meta?.Publisher) ? WingetCatalogService.PublisherFromId(id) : meta.Publisher!,
                    Version = meta?.Version ?? "",
                    Category = "Other",
                    Description = !string.IsNullOrWhiteSpace(meta?.Description) ? meta.Description!
                        : _wingetIndex.IsAvailable ? "Not in the official winget index; winget will report if it cannot be found."
                        : "Not verified (official index unavailable).",
                });
            }

            if (!_wingetQueue.Contains(item)) added++;
            SetQueued(item, true);
        }
        return (added, notInIndex);
    }

    // ---------------- Packs ----------------
    private void ReloadWingetPackCombo(string? selectName)
    {
        var packs = _winget.Packs;
        WingetPackCombo.ItemsSource = packs;
        var idx = selectName is null ? 0 : packs.FindIndex(p => p.IsCustom && p.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase));
        WingetPackCombo.SelectedIndex = packs.Count == 0 ? -1 : Math.Max(0, idx);
    }

    private void OnWingetPackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        WingetDeletePackBtn.IsEnabled = WingetPackCombo.SelectedItem is WingetPack { IsCustom: true };
        if (WingetPackCombo.SelectedItem is WingetPack p)
            WingetPackCombo.ToolTip = $"{p.Description}\n{string.Join(", ", p.PackageIds)}";
    }

    private void OnApplyWingetPackClick(object sender, RoutedEventArgs e)
    {
        if (WingetPackCombo.SelectedItem is not WingetPack pack) return;
        var (added, notInIndex) = AddIdsToQueue(pack.PackageIds, null);
        var msg = $"Pack '{pack.Name}': {pack.PackageIds.Count} apps, {added} added to the queue.";
        if (notInIndex.Count > 0) msg += $" Not in the official index: {string.Join(", ", notInIndex)}.";
        DeployLog("packs", null, msg);
        StatusText.Text = msg;
    }

    private void OnSaveWingetPackClick(object sender, RoutedEventArgs e)
    {
        var name = WingetPackNameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "Enter a pack name in the box next to Save Pack.";
            WingetPackNameBox.Focus();
            return;
        }
        if (_winget.Presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"'{name}' is a built-in preset name; choose another name.";
            return;
        }
        try
        {
            var replaced = _winget.CustomPacks.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var pack = _winget.SaveCustomPack(name, _wingetQueue.Select(a => a.Id).ToList());
            ReloadWingetPackCombo(pack.Name);
            var msg = $"{(replaced ? "Updated" : "Saved")} custom pack '{pack.Name}' with {pack.PackageIds.Count} apps.";
            DeployLog("packs", null, msg);
            StatusText.Text = msg;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save pack: {ex.Message}";
            DeployLog("packs", null, $"Save failed: {ex.Message}");
        }
    }

    private void OnDeleteWingetPackClick(object sender, RoutedEventArgs e)
    {
        if (WingetPackCombo.SelectedItem is not WingetPack { IsCustom: true } pack) return;
        if (MessageBox.Show(this, $"Delete the custom pack '{pack.Name}'?", "Home RMS - Packs",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            _winget.DeleteCustomPack(pack.Name);
            ReloadWingetPackCombo(null);
            StatusText.Text = $"Deleted custom pack '{pack.Name}'.";
            DeployLog("packs", null, StatusText.Text);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not delete pack: {ex.Message}";
            DeployLog("packs", null, $"Delete failed: {ex.Message}");
        }
    }

    // ---------------- Import ----------------
    private void OnToggleWingetImportClick(object sender, RoutedEventArgs e)
    {
        var show = WingetImportPanel.Visibility != Visibility.Visible;
        WingetImportPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) WingetImportBox.Focus();
    }

    private void OnPasteWingetImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                WingetImportResultText.Text = "The clipboard has no text.";
                return;
            }
            WingetImportBox.Text = text;
        }
        catch (Exception ex)
        {
            WingetImportResultText.Text = $"Could not read the clipboard: {ex.Message}";
        }
    }

    private async void OnRunWingetImportClick(object sender, RoutedEventArgs e)
    {
        var text = WingetImportBox.Text.Trim();
        if (text.Length == 0)
        {
            WingetImportResultText.Text = "Paste a winstall pack URL, a winget script, JSON or a list of IDs first.";
            return;
        }

        PackImportResult result;
        WingetImportRunBtn.IsEnabled = false;
        try
        {
            if (WinstallImporter.TryGetPackId(text, out var packId))
            {
                WingetImportResultText.Text = $"Fetching winstall pack {packId}...";
                result = await WinstallImporter.FetchPackAsync(packId);
            }
            else
            {
                result = WinstallImporter.ParseText(text);
            }
        }
        catch (Exception ex)
        {
            var msg = ex is TaskCanceledException ? "winstall did not answer in time. Paste the pack's generated winget script instead." : ex.Message;
            WingetImportResultText.Text = $"Import failed: {msg}";
            DeployLog("import", null, $"Import failed: {msg}");
            return;
        }
        finally
        {
            WingetImportRunBtn.IsEnabled = true;
        }

        if (result.Ids.Count == 0 && result.Rejected.Count == 0)
        {
            WingetImportResultText.Text = $"No package IDs found ({result.Format}). Supported: winstall pack URL, 'winget install --id=X -e' scripts, winget export JSON, {{\"apps\":[{{\"id\":...}}]}} JSON, one ID per line.";
            return;
        }

        var (added, notInIndex) = AddIdsToQueue(result.Ids, result);
        var parts = new List<string>
        {
            $"{result.Format}{(string.IsNullOrWhiteSpace(result.PackName) ? "" : $" '{result.PackName}'")}: {result.Ids.Count} valid ID(s), {added} added to the queue.",
        };
        if (notInIndex.Count > 0)
            parts.Add($"Not in the official index (queued anyway): {string.Join(", ", notInIndex)}.");
        if (result.Rejected.Count > 0)
            parts.Add($"Rejected (not valid winget IDs): {string.Join(", ", result.Rejected)}.");
        var summary = string.Join(" ", parts);
        WingetImportResultText.Text = summary;
        DeployLog("import", null, summary);

        if (!string.IsNullOrWhiteSpace(result.PackName) && string.IsNullOrWhiteSpace(WingetPackNameBox.Text))
            WingetPackNameBox.Text = result.PackName!.Length > 60 ? result.PackName[..60] : result.PackName;
    }

    // ---------------- Targets ----------------
    private void OnRefreshDeployTargetsClick(object sender, RoutedEventArgs e) => RefreshDeployTargets();

    private void RefreshDeployTargets()
    {
        var wasChecked = _deployTargets.Where(t => t.IsChecked).Select(t => t.Profile).ToHashSet();
        _deployTargets.Clear();
        foreach (var m in _machines.Where(m => m.IsWindows))
        {
            var mode = m.IsAgentless ? "WinRM" : "agent";
            var state = m.IsConnected ? "connected" : m.IsOnline ? "online" : "offline";
            _deployTargets.Add(new DeployTarget { Profile = m, Detail = $"  {mode} · {state}", IsChecked = wasChecked.Contains(m) });
        }
        DeployTargetHintText.Text = _deployTargets.Count == 0
            ? "No saved Windows endpoints: deploys go to the connected session."
            : "Nothing checked = the connected (or selected WinRM) endpoint. Check several to deploy to each in turn.";
    }

    private async Task<List<WingetTarget>?> ResolveWingetTargetsAsync()
    {
        var picked = _deployTargets.Where(t => t.IsChecked).Select(t => t.Profile).ToList();
        if (picked.Count > 0)
            return picked.Select(p => new WingetTarget(p.DisplayTitle, p, p.IsAgentless)).ToList();

        if (SelectedMachine is { IsAgentless: true } agentless)
            return [new WingetTarget(agentless.DisplayTitle, agentless, true)];

        if (_conn is null && !await ConnectToCurrentFieldsAsync())
            return null;
        return [new WingetTarget(_agentInfo?.MachineName ?? _connectedProfile?.DisplayTitle ?? _conn!.Host, _connectedProfile, false)];
    }

    // ---------------- Deploy ----------------
    private void OnCancelWingetDeployClick(object sender, RoutedEventArgs e)
    {
        if (_wingetDeployCts is null) return;
        _wingetDeployCts.Cancel();
        WingetCancelDeployBtn.IsEnabled = false;
        DeployStatusBadge.Text = "CANCELLING...";
        DeployLog("winget", null, "Cancellation requested.");
    }

    private async void OnDeploySelectedWingetClick(object sender, RoutedEventArgs e)
    {
        if (_wingetDeployCts is not null) return;
        var items = _wingetQueue.Where(i => WingetRules.IsValidPackageId(i.Id)).ToList();
        if (items.Count == 0)
        {
            StatusText.Text = "Add at least one app to the deployment queue.";
            return;
        }

        List<WingetTarget>? targets;
        try
        {
            targets = await ResolveWingetTargetsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not resolve deployment targets: {ex.Message}";
            return;
        }
        if (targets is null || targets.Count == 0) return;
        var targetCount = targets.Count;

        var silent = WingetSilentCheck.IsChecked == true;
        var machineScope = WingetMachineScopeCheck.IsChecked == true;
        var upgrade = WingetUpgradeCheck.IsChecked == true;

        var cts = _wingetDeployCts = new CancellationTokenSource();
        WingetDeploySelectedBtn.IsEnabled = false;
        WingetCancelDeployBtn.IsEnabled = true;
        DeployStatusBadge.Text = "DEPLOYING...";

        var outcomes = items.ToDictionary(i => i, _ => new List<WingetOutcome>());
        void Record(WingetAppItem item, string target, DeployItemStatus status, string detail)
        {
            if (outcomes.TryGetValue(item, out var l)) l.Add(new WingetOutcome(target, status, detail));
            item.DeployStatus = status;
            item.Percent = -1;
            item.StatusDetail = targetCount > 1 ? $"{target}: {detail}" : detail;
        }

        foreach (var item in items)
        {
            item.ResetDeployState();
            item.DeployStatus = DeployItemStatus.Queued;
        }
        DeployLog("winget", null, $"Deploying {items.Count} package(s) to {targets.Count} endpoint(s): {string.Join(", ", targets.Select(t => t.Label))} " +
                                  $"(silent={silent}, machine scope={machineScope}, upgrade if installed={upgrade}).");

        try
        {
            foreach (var target in targets)
            {
                if (cts.IsCancellationRequested) break;
                foreach (var item in items)
                {
                    item.Percent = -1;
                    item.DeployStatus = DeployItemStatus.Queued;
                }

                if (target.Agentless)
                    await DeployWingetAgentlessAsync(target, items, silent, machineScope, upgrade, Record, cts.Token);
                else
                    await DeployWingetViaAgentAsync(target, items, silent, machineScope, upgrade, Record, cts.Token);
            }
        }
        catch (Exception ex)
        {
            DeployLog("winget", null, $"Deployment aborted: {ex.Message}");
        }
        finally
        {
            int ok = 0, skipped = 0, failed = 0, cancelled = 0;
            foreach (var item in items)
            {
                var list = outcomes[item];
                var reported = list.Select(o => o.Target).ToHashSet();
                foreach (var t in targets.Where(t => !reported.Contains(t.Label)))
                    list.Add(new WingetOutcome(t.Label, DeployItemStatus.Cancelled, "Not run (cancelled)"));

                var status = list.Any(o => o.Status == DeployItemStatus.Failed) ? DeployItemStatus.Failed
                    : list.Any(o => o.Status == DeployItemStatus.Cancelled) ? DeployItemStatus.Cancelled
                    : list.All(o => o.Status == DeployItemStatus.Skipped) ? DeployItemStatus.Skipped
                    : DeployItemStatus.Success;
                item.Percent = -1;
                item.DeployStatus = status;
                item.StatusDetail = targets.Count > 1
                    ? string.Join("\n", list.Select(o => $"{o.Target}: {o.Detail}"))
                    : list.FirstOrDefault()?.Detail ?? "";

                switch (status)
                {
                    case DeployItemStatus.Success: ok++; break;
                    case DeployItemStatus.Skipped: skipped++; break;
                    case DeployItemStatus.Failed: failed++; break;
                    default: cancelled++; break;
                }
            }

            var wasCancelled = cts.IsCancellationRequested;
            _wingetDeployCts = null;
            cts.Dispose();
            WingetDeploySelectedBtn.IsEnabled = true;
            WingetCancelDeployBtn.IsEnabled = false;
            DeployStatusBadge.Text = wasCancelled ? "CANCELLED" : failed > 0 ? "COMPLETED WITH ERRORS" : "COMPLETE";
            var summary = $"Finished: {ok} installed, {skipped} up to date, {failed} failed, {cancelled} cancelled.";
            DeployLog("winget", null, summary);
            StatusText.Text = $"Winget deployment {summary}";
        }
    }

    private async Task DeployWingetViaAgentAsync(WingetTarget target, List<WingetAppItem> items,
        bool silent, bool machineScope, bool upgrade,
        Action<WingetAppItem, string, DeployItemStatus, string> record, CancellationToken ct)
    {
        var byId = items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RmsConnection? owned = null;
        string? failure = null;
        var failureStatus = DeployItemStatus.Failed;

        try
        {
            RmsConnection conn;
            var p = target.Profile;
            if (_conn is not null && (p is null || p == _connectedProfile ||
                                      (string.Equals(_conn.Host, p.Host, StringComparison.OrdinalIgnoreCase) && _conn.Port == p.Port)))
            {
                conn = _conn;
            }
            else if (p is not null)
            {
                owned = new RmsConnection(p.Host, p.Port);
                conn = owned;
            }
            else
            {
                failure = "Not connected to an agent.";
                return;
            }

            foreach (var chunk in items.Chunk(WingetRules.MaxPackagesPerRequest))
            {
                ct.ThrowIfCancellationRequested();
                var request = new WingetRequest { Silent = silent, MachineScope = machineScope, UpgradeIfInstalled = upgrade };
                request.PackageIds.AddRange(chunk.Select(i => i.Id));
                DeployLog(target.Label, null, $"RunWinget: {string.Join(", ", chunk.Select(i => i.Id))}");

                using var call = conn.Client.RunWinget(request, cancellationToken: ct);
                await foreach (var progress in call.ResponseStream.ReadAllAsync(ct))
                {
                    byId.TryGetValue(progress.PackageId ?? "", out var item);
                    if (!string.IsNullOrEmpty(progress.LogLine))
                        DeployLog(target.Label, string.IsNullOrEmpty(progress.PackageId) ? null : progress.PackageId, progress.LogLine);

                    if (item is not null)
                    {
                        switch (progress.Phase)
                        {
                            case WingetPhase.Queued:
                                item.DeployStatus = DeployItemStatus.Queued;
                                break;
                            case WingetPhase.Downloading:
                                item.DeployStatus = DeployItemStatus.Downloading;
                                item.Percent = progress.Percent;
                                break;
                            case WingetPhase.Installing:
                                item.DeployStatus = DeployItemStatus.Installing;
                                item.Percent = progress.Percent;
                                break;
                            case WingetPhase.Succeeded:
                            case WingetPhase.Failed:
                            case WingetPhase.Skipped:
                                if (finished.Add(item.Id))
                                {
                                    var status = progress.Phase switch
                                    {
                                        WingetPhase.Succeeded => DeployItemStatus.Success,
                                        WingetPhase.Skipped => DeployItemStatus.Skipped,
                                        _ => DeployItemStatus.Failed,
                                    };
                                    var detail = DescribeExit(progress.ExitCode, progress.ExitMessage);
                                    record(item, target.Label, status, detail);
                                    DeployLog(target.Label, item.Id, $"{progress.Phase}: {detail}");
                                }
                                break;
                        }
                    }
                    if (progress.Done) break;
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated)
        {
            failure = $"Endpoint refused the request ({ex.StatusCode}): pair this endpoint first. Connect to it from Fleet and enter the 2FA code shown on the agent so it pins this Controller, then retry.";
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled || ct.IsCancellationRequested)
        {
            failure = "Cancelled.";
            failureStatus = DeployItemStatus.Cancelled;
        }
        catch (OperationCanceledException)
        {
            failure = "Cancelled.";
            failureStatus = DeployItemStatus.Cancelled;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            failure = "This agent does not support RunWinget; update the agent to v2.1 or newer.";
        }
        catch (RpcException ex)
        {
            failure = $"{ex.StatusCode}: {ex.Status.Detail}";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            owned?.Dispose();
        }

        if (failure is not null)
            DeployLog(target.Label, null, failure);
        foreach (var item in items.Where(i => !finished.Contains(i.Id)))
            record(item, target.Label, failure is null ? DeployItemStatus.Failed : failureStatus,
                failure ?? "The agent ended the stream without a result for this package.");
    }

    private async Task DeployWingetAgentlessAsync(WingetTarget target, List<WingetAppItem> items,
        bool silent, bool machineScope, bool upgrade,
        Action<WingetAppItem, string, DeployItemStatus, string> record, CancellationToken ct)
    {
        var profile = target.Profile!;
        if (string.IsNullOrWhiteSpace(profile.Username))
        {
            const string msg = "WinRM credentials are missing for this endpoint (edit it in Fleet).";
            DeployLog(target.Label, null, msg);
            foreach (var item in items) record(item, target.Label, DeployItemStatus.Failed, msg);
            return;
        }

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested)
            {
                record(item, target.Label, DeployItemStatus.Cancelled, "Cancelled.");
                continue;
            }

            item.DeployStatus = DeployItemStatus.Installing;
            item.Percent = -1;
            DeployLog(target.Label, item.Id, "Running winget over WinRM...");
            try
            {
                var script = WingetCatalogService.BuildWingetInstallScript(item.Id, silent, machineScope, upgrade);
                var (_, stdout, stderr) = await _agentless.InvokeWinRmAsync(profile, script, ct, timeoutMs: 30 * 60 * 1000);

                foreach (var line in stdout.Split('\n'))
                    if (!line.Contains(WingetCatalogService.ExitMarker, StringComparison.Ordinal))
                        DeployLog(target.Label, item.Id, line);
                foreach (var line in stderr.Split('\n'))
                    DeployLog(target.Label, item.Id, line.Trim().Length == 0 ? "" : "stderr: " + line);

                if (ct.IsCancellationRequested)
                {
                    record(item, target.Label, DeployItemStatus.Cancelled, "Cancelled.");
                    continue;
                }

                var code = WingetCatalogService.ParseExitMarker(stdout);
                if (code is null)
                {
                    var why = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no exit-code marker in the WinRM output";
                    record(item, target.Label, DeployItemStatus.Failed, $"WinRM run failed: {why}");
                    continue;
                }

                var status = WingetRules.IsSuccess(code.Value) ? DeployItemStatus.Success
                    : WingetRules.IsAlreadyInstalled(code.Value) ? DeployItemStatus.Skipped
                    : DeployItemStatus.Failed;
                var detail = DescribeExit(code.Value, null);
                record(item, target.Label, status, detail);
                DeployLog(target.Label, item.Id, detail);
            }
            catch (Exception ex)
            {
                record(item, target.Label, ct.IsCancellationRequested ? DeployItemStatus.Cancelled : DeployItemStatus.Failed, ex.Message);
                DeployLog(target.Label, item.Id, $"ERROR: {ex.Message}");
            }
        }
    }

    private static string DescribeExit(int exitCode, string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? WingetRules.Describe(exitCode) : message;
        var hex = $"0x{unchecked((uint)exitCode):X8}";
        return text.Contains(hex, StringComparison.OrdinalIgnoreCase) || exitCode == 0
            ? $"exit {exitCode}: {text}"
            : $"exit {exitCode} ({hex}): {text}";
    }

    /// <summary>Timestamped log line with endpoint / package prefix; control characters are stripped.</summary>
    private void DeployLog(string scope, string? package, string message)
    {
        var clean = new string(message.Where(c => c == '\t' || !char.IsControl(c)).ToArray()).TrimEnd();
        if (clean.Length == 0) return;
        AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] [{scope}]{(package is null ? "" : $" [{package}]")} {clean}");
    }

    // ---------------- File deploy list ----------------
    private static readonly string[] DeployableExtensions = [".exe", ".msi", ".msp", ".ps1", ".cmd", ".bat"];

    private static bool IsDeployableFile(string path) =>
        DeployableExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private DeployFileItem? AddDeployFile(string path)
    {
        if (!System.IO.File.Exists(path) || !IsDeployableFile(path))
        {
            DeployLog("file", null, $"Skipped '{path}': only existing .exe, .msi, .msp, .ps1, .cmd and .bat files can be deployed.");
            return null;
        }
        var existing = _deployFiles.FirstOrDefault(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!existing.IsBusy) { existing.DeployStatus = DeployItemStatus.Pending; existing.Detail = ""; existing.Percent = -1; }
            return existing;
        }
        var item = new DeployFileItem { FullPath = path };
        item.Detail = $"{new System.IO.FileInfo(path).Length / 1024.0 / 1024.0:F2} MB";
        _deployFiles.Add(item);
        return item;
    }

    private void OnRemoveDeployFileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeployFileItem { IsBusy: false } item)
            _deployFiles.Remove(item);
    }

    private void OnClearDeployFilesClick(object sender, RoutedEventArgs e)
    {
        foreach (var f in _deployFiles.Where(f => !f.IsBusy).ToList())
            _deployFiles.Remove(f);
    }

    /// <summary>Uploads and runs every pending file in the list, one after another, on the connected agent.</summary>
    private async Task RunPendingDeployFilesAsync()
    {
        if (_fileDeployRunning) return;
        var pending = _deployFiles.Where(f => f.DeployStatus is DeployItemStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            StatusText.Text = "No pending files: drop installers/scripts here or use Browse.";
            return;
        }

        _fileDeployRunning = true;
        try
        {
            foreach (var f in pending)
            {
                if (!_deployFiles.Contains(f)) continue;
                if (!await DeployLocalFileCoreAsync(f, DeployArgsBox.Text.Trim()))
                {
                    if (_conn is null) break; // not connected: stop instead of failing every file
                }
            }
        }
        finally
        {
            _fileDeployRunning = false;
        }
    }

    private bool _fileDeployRunning;
}
