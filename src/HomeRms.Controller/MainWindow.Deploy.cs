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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Controller.Views.Deploy;
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

/// <summary>
/// Deploy tab, catalog side: winstall-style Discover / Apps / Essentials / app page / Packs and the shared
/// selection (one <see cref="_selectedApps"/> collection for every sub-view). Data: api.winstall.app (cached),
/// the official winget index, and the built-in curated list as the offline fallback.
/// Install view, scripts, targets and the actual deployment live in MainWindow.DeployRun.cs.
/// </summary>
public partial class MainWindow
{
    private const int AppsPageSize = 60;
    private const int TrendingCount = 12;
    private const int CommunityPageSize = 12;
    private const string BuiltInSourceLabel = "built-in curated list";

    private readonly WingetCatalogService _winget = new();
    private readonly WingetSourceIndex _wingetIndex = new();
    private readonly WingetIconCache _wingetIcons = new();
    private readonly WinstallApiClient _winstall = new();

    /// <summary>The selection shared by every Deploy sub-view (cards, bar, Install view, deploy queue).</summary>
    private readonly ObservableCollection<WingetAppItem> _selectedApps = [];
    private readonly ObservableCollection<WingetAppItem> _appsResults = [];
    private readonly ObservableCollection<WingetAppItem> _trendingApps = [];
    private readonly ObservableCollection<PackCard> _discoverPacks = [];
    private readonly ObservableCollection<PackCard> _featuredPacks = [];
    private readonly ObservableCollection<PackCard> _communityPacks = [];
    private readonly ObservableCollection<DeployTarget> _deployTargets = [];
    private readonly ObservableCollection<DeployFileItem> _deployFiles = [];
    private readonly DispatcherTimer _wingetSearchDebounce = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly SemaphoreSlim _detailsGate = new(4, 4);

    private CancellationTokenSource? _wingetSearchCts;
    private CancellationTokenSource? _wingetDeployCts;
    private int _wingetSearchGeneration;
    private int _appsOffset;
    private int _appsTotal;
    private bool _appsCanPage;
    private bool _appsLoading;
    private bool _appsLoaded;
    private int _communityOffset;
    private int _communityTotal;
    private bool _packsLoaded;
    private bool _syncingSearch;
    private bool _wingetIndexLoadStarted;
    private bool _discoverLoadStarted;
    private bool _deployInitialized;
    private Task _discoverTask = Task.CompletedTask;
    private Task _appsTask = Task.CompletedTask;
    private Task _packsTask = Task.CompletedTask;
    private Task _detailTask = Task.CompletedTask;

    // Names the rest of MainWindow (file deploy, log, screenshots) has always used.
    private TextBlock DeployStatusBadge => DeployShell.StatusBadge;
    private TextBox DeployOutputBox => DeployShell.Install.OutputBox;
    private TextBox DeployFilePathBox => DeployShell.Files.PathBox;
    private TextBox DeployArgsBox => DeployShell.Files.ArgsBox;
    private Button DeployRunBtn => DeployShell.Files.RunFilesBtn;

    private DiscoverView DiscoverV => DeployShell.Discover;
    private AppsView AppsV => DeployShell.Apps;
    private AppDetailView DetailV => DeployShell.Detail;
    private PacksView PacksV => DeployShell.Packs;
    private InstallView InstallV => DeployShell.Install;
    private FilesView FilesV => DeployShell.Files;
    private SelectionBar BarV => DeployShell.Selection;

    private sealed record WingetTarget(string Label, MachineProfile? Profile, bool Agentless);
    private sealed record WingetOutcome(string Target, DeployItemStatus Status, string Detail);

    private void InitializeDeploy()
    {
        // Lists
        AppsV.AppsList.ItemsSource = _appsResults;
        DiscoverV.TrendingList.ItemsSource = _trendingApps;
        DiscoverV.FeaturedPacksList.ItemsSource = _discoverPacks;
        DiscoverV.CategoryList.ItemsSource = DeployCategory.Top.Select(MakeTile).ToList();
        PacksV.FeaturedPacksList.ItemsSource = _featuredPacks;
        PacksV.CommunityPacksList.ItemsSource = _communityPacks;
        InstallV.SelectedList.ItemsSource = _selectedApps;
        InstallV.QueueList.ItemsSource = _selectedApps;
        InstallV.TargetList.ItemsSource = _deployTargets;
        FilesV.FileList.ItemsSource = _deployFiles;
        BarV.Bind(_selectedApps);
        DeployShell.Essentials.GroupsList.ItemsSource = _winget.BuiltIn
            .GroupBy(a => a.Category)
            .Select(g => new EssentialsGroup(EssentialsHeading(g.Key), g.ToList()))
            .ToList();
        foreach (var app in _winget.BuiltIn) _wingetIcons.Attach(app);

        // Commands raised by cards anywhere in the shell
        var shell = DeployShell;
        shell.CommandBindings.Add(new CommandBinding(DeployCommands.OpenApp, (_, e) => { if (e.Parameter is WingetAppItem a) OpenAppPage(a); }));
        shell.CommandBindings.Add(new CommandBinding(DeployCommands.ToggleApp, (_, e) => { if (e.Parameter is WingetAppItem a) SetQueued(a, !a.IsSelected); }));
        shell.CommandBindings.Add(new CommandBinding(DeployCommands.OpenCategory, (_, e) => { if (e.Parameter is string s) OpenCategory(s); }));
        shell.CommandBindings.Add(new CommandBinding(DeployCommands.SelectPack, async (_, e) => { if (e.Parameter is PackCard p) await SelectPackAsync(p); }));
        shell.CommandBindings.Add(new CommandBinding(DeployCommands.DeletePack, (_, e) => { if (e.Parameter is PackCard p) DeleteCustomPack(p); }));
        shell.CommandBindings.Add(new CommandBinding(DeployCommands.AddSelectionToPack, (_, e) => { if (e.Parameter is WingetPack p) AddSelectionToPack(p); }));
        shell.ViewShown += OnDeployViewShown;

        // Discover
        DiscoverV.BrowseAppsBtn.Click += (_, _) => shell.Show(DeployView.Apps);
        DiscoverV.ViewAllAppsBtn.Click += (_, _) => shell.Show(DeployView.Apps);
        DiscoverV.OpenPacksBtn.Click += (_, _) => shell.Show(DeployView.Packs);
        DiscoverV.ViewAllPacksBtn.Click += (_, _) => shell.Show(DeployView.Packs);
        DiscoverV.RefreshIndexBtn.Click += OnRefreshWingetIndexClick;

        // Search: the top-bar box and the Apps box stay in sync
        shell.TopSearchBox.TextChanged += (_, _) =>
        {
            if (_syncingSearch) return;
            _syncingSearch = true;
            AppsV.SearchBox.Text = shell.TopSearchBox.Text;
            _syncingSearch = false;
            if (shell.Current != DeployView.Apps && shell.TopSearchBox.Text.Trim().Length > 0) shell.Show(DeployView.Apps);
            RestartSearchDebounce();
        };
        shell.TopSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            shell.Show(DeployView.Apps);
            _wingetSearchDebounce.Stop();
            _appsTask = RunAppsSearchAsync(append: false);
        };
        AppsV.SearchBox.TextChanged += (_, _) =>
        {
            if (_syncingSearch) return;
            _syncingSearch = true;
            shell.TopSearchBox.Text = AppsV.SearchBox.Text;
            _syncingSearch = false;
            RestartSearchDebounce();
        };
        AppsV.CategoryChanged += (_, _) => { _wingetSearchDebounce.Stop(); _appsTask = RunAppsSearchAsync(append: false); };
        AppsV.NearEnd += (_, _) => { if (_appsCanPage && !_appsLoading && _appsOffset < _appsTotal) _appsTask = RunAppsSearchAsync(append: true); };
        AppsV.LoadMoreBtn.Click += (_, _) => _appsTask = RunAppsSearchAsync(append: true);
        _wingetSearchDebounce.Tick += (_, _) =>
        {
            _wingetSearchDebounce.Stop();
            _appsTask = RunAppsSearchAsync(append: false);
        };

        // App page
        DetailV.BackBtn.Click += (_, _) => shell.Show(shell.Previous);
        DetailV.CopyBtn.Click += (_, _) => CopyText(DetailV.CommandBox.Text, () => DetailV.CopyText.Text = "Copied!");
        DetailV.InstallNowBtn.Click += (_, _) =>
        {
            if (DetailV.Item is { } item) SetQueued(item, true);
            shell.Show(DeployView.Install);
        };

        // Packs
        PacksV.ImportToggleBtn.Click += OnToggleWingetImportClick;
        PacksV.ImportCloseBtn.Click += OnToggleWingetImportClick;
        PacksV.ImportPasteBtn.Click += OnPasteWingetImportClick;
        PacksV.ImportRunBtn.Click += OnRunWingetImportClick;
        PacksV.NewPackToggleBtn.Click += (_, _) => ToggleNewPackPanel(true);
        PacksV.NewPackCloseBtn.Click += (_, _) => ToggleNewPackPanel(false);
        PacksV.SavePackBtn.Click += (_, _) => SaveSelectionAsPack(PacksV.PackNameBox.Text, PacksV.StatusText);
        PacksV.LoadMoreBtn.Click += (_, _) => _packsTask = LoadCommunityPacksAsync(append: true);

        // Selection bar
        BarV.ClearBtn.Click += OnClearWingetQueueClick;
        BarV.InstallBtn.Click += (_, _) => { BarV.CloseFlyouts(); shell.Show(DeployView.Install); };
        BarV.SaveNewPackBtn.Click += (_, _) => SaveSelectionAsPack(BarV.NewPackNameBox.Text, BarV.PackResultText);
        BarV.NewPackNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveSelectionAsPack(BarV.NewPackNameBox.Text, BarV.PackResultText); };

        InitializeInstallView(); // MainWindow.DeployRun.cs

        _selectedApps.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (WingetAppItem a in e.NewItems) a.OptionsChanged += OnSelectedAppOptionsChanged;
            if (e.OldItems is not null) foreach (WingetAppItem a in e.OldItems) a.OptionsChanged -= OnSelectedAppOptionsChanged;
            UpdateWingetSelectedCount();
        };

        if (_winget.PackLoadError is { } packErr)
            DeployLog("packs", null, packErr);

        // Offline-first content until api.winstall.app answers
        foreach (var app in _winget.BuiltIn.Take(TrendingCount)) _trendingApps.Add(app);
        foreach (var card in _winget.Presets.Take(4).Select(CardFromPack)) _discoverPacks.Add(card);
        RefreshPackLists();

        _deployInitialized = true;
        UpdateWingetSourceStatus();
        UpdateWingetSelectedCount();
    }

    private static string EssentialsHeading(string category) => category switch
    {
        "Developer" => "Developer Tools",
        "Admin" => "Admin & Diagnostics",
        "Media" => "Media & Entertainment",
        _ => category,
    };

    private CategoryTile MakeTile(DeployCategory c) => new(c.Slug, c.Label,
        Themes.Palette.Get($"DeployTone{c.Tone}FillBrush"), Themes.Palette.Get($"DeployTone{c.Tone}BadgeBrush"),
        Themes.Palette.Get($"DeployTone{c.Tone}InkBrush"), (Geometry)FindResource(c.IconKey));

    private void OnDeployPanelVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_deployInitialized || e.NewValue is not true) return;
        RefreshDeployTargets();
        if (!_wingetIndexLoadStarted)
        {
            _wingetIndexLoadStarted = true;
            _ = EnsureWingetIndexAsync(force: false);
        }
        if (!_discoverLoadStarted)
        {
            _discoverLoadStarted = true;
            _discoverTask = LoadDiscoverAsync();
        }
    }

    private void OnDeployViewShown(object? sender, DeployView view)
    {
        BarV.CloseFlyouts();
        switch (view)
        {
            case DeployView.Apps when !_appsLoaded:
                _appsTask = RunAppsSearchAsync(append: false);
                break;
            case DeployView.Packs:
                RefreshPackLists();
                if (!_packsLoaded) { _packsLoaded = true; _packsTask = LoadPacksAsync(); }
                break;
            case DeployView.Install:
                PrepareInstallView(); // MainWindow.DeployRun.cs
                break;
        }
    }

    // ---------------- Catalog source (official index) ----------------
    private async Task EnsureWingetIndexAsync(bool force)
    {
        DiscoverV.IndexStatusText.Text = force
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
                (_wingetIndex.IsAvailable ? " (keeping the cached copy)" : " (falling back to api.winstall.app / built-in list)"));
        }

        RefreshBuiltInVersionsFromIndex();
        UpdateWingetSourceStatus();
        DeployStatusBadge.Text = _wingetDeployCts is null ? "READY" : "DEPLOYING...";
        if (_appsLoaded && _appsResults.Count == 0) _appsTask = RunAppsSearchAsync(append: false);
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
            text = $"Official winget index (cdn.winget.microsoft.com), updated {updated}.";
            if (s.Error is not null) text += $" Last refresh failed: {s.Error} (using the cached copy).";
            DiscoverV.PackageCountText.Text = s.PackageCount.ToString("N0");
            DeployShell.TopSearchBox.Tag = $"Search {s.PackageCount / 50 * 50:N0}+ apps";
        }
        else if (s.Error is not null)
        {
            text = $"Official winget index unavailable: {s.Error}. Browsing uses api.winstall.app, then the built-in list.";
            DiscoverV.PackageCountText.Text = _appsTotal > 0 ? _appsTotal.ToString("N0") : "—";
        }
        else
        {
            text = "Official winget index not downloaded yet; it loads when this tab opens.";
        }
        DiscoverV.IndexStatusText.Text = text;
        DiscoverV.IndexStatusText.ToolTip = text;
    }

    private void OnRefreshWingetIndexClick(object sender, RoutedEventArgs e)
    {
        _wingetIndexLoadStarted = true;
        _ = EnsureWingetIndexAsync(force: true);
    }

    // ---------------- Discover ----------------
    private async Task LoadDiscoverAsync()
    {
        var notes = new List<string>();
        try
        {
            var page = await _winstall.TrendingAppsAsync();
            var items = page.Items.Select(_winget.FromWinstall).Take(TrendingCount).ToList();
            if (items.Count > 0)
            {
                _trendingApps.Clear();
                foreach (var i in items) { _trendingApps.Add(i); _wingetIcons.Attach(i); }
                DiscoverV.TrendingSubtitle.Text = "Popular downloads this week.";
            }
            if (page.Stale) notes.Add("trending apps are from the offline cache");
        }
        catch (Exception ex)
        {
            DiscoverV.TrendingSubtitle.Text = "Curated favourites (winstall trending is unavailable).";
            notes.Add($"trending apps unavailable ({Reason(ex)}), showing the built-in list");
        }

        try
        {
            var packs = await _winstall.TrendingPacksAsync();
            var cards = packs.Items.Take(4).Select(CardFromWinstall).ToList();
            if (cards.Count > 0)
            {
                _discoverPacks.Clear();
                foreach (var c in cards) _discoverPacks.Add(c);
            }
            if (packs.Stale) notes.Add("featured packs are from the offline cache");
        }
        catch (Exception ex)
        {
            notes.Add($"featured packs unavailable ({Reason(ex)}), showing Home RMS presets");
        }

        DiscoverV.OfflineNote.Text = notes.Count == 0 ? "" : "Offline: " + string.Join("; ", notes) + ".";
        DiscoverV.OfflineNote.Visibility = notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var n in notes) DeployLog("catalog", null, n);
    }

    private static string Reason(Exception ex) => ex is TaskCanceledException ? "timed out" : ex.Message;

    private void OpenCategory(string slug)
    {
        AppsV.SetCategory(slug);
        _syncingSearch = true;
        AppsV.SearchBox.Text = "";
        DeployShell.TopSearchBox.Text = "";
        _syncingSearch = false;
        DeployShell.Show(DeployView.Apps);
        _wingetSearchDebounce.Stop();
        _appsTask = RunAppsSearchAsync(append: false);
    }

    // ---------------- Apps (search / browse) ----------------
    private void RestartSearchDebounce()
    {
        if (!_deployInitialized) return;
        _wingetSearchDebounce.Stop();
        _wingetSearchDebounce.Start();
    }

    private async Task RunAppsSearchAsync(bool append)
    {
        var query = AppsV.SearchBox.Text.Trim();
        var category = DeployCategory.Find(AppsV.CategorySlug);
        var gen = ++_wingetSearchGeneration;
        _wingetSearchCts?.Cancel();
        var cts = _wingetSearchCts = new CancellationTokenSource();
        var offset = append ? _appsOffset : 0;
        _appsLoading = true;
        _appsLoaded = true;
        if (!append) AppsV.ResultText.Text = "Loading...";

        List<WingetAppItem>? page = null;
        var total = 0;
        var source = "";
        var canPage = false;
        var problems = new List<string>();

        try
        {
            // 1. api.winstall.app (descriptions, popularity order) - cached on disk.
            try
            {
                var result = query.Length > 0 ? await _winstall.SearchAppsAsync(query, offset, AppsPageSize, cts.Token)
                    : category is not null ? await _winstall.CategoryAppsAsync(category.Slug, offset, AppsPageSize, cts.Token)
                    : await _winstall.ListAppsAsync(offset, AppsPageSize, cts.Token);
                if (gen != _wingetSearchGeneration) return;
                page = result.Items.Select(_winget.FromWinstall).ToList();
                total = result.Total;
                canPage = true;
                source = result.Stale ? "api.winstall.app (offline cache)" : "api.winstall.app";
                if (query.Length > 0 && category is not null)
                {
                    page = page.Where(a => a.Category == category.Label).ToList();
                    source += $" · filtered to {category.Label}";
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                problems.Add($"api.winstall.app: {Reason(ex)}");
            }

            // 2. Official winget index (offline after the first download).
            if (page is null && _wingetIndex.IsAvailable)
            {
                try
                {
                    var (rows, t) = await Task.Run(() => _wingetIndex.Search(query, category?.FallbackTags, offset, AppsPageSize), cts.Token);
                    if (gen != _wingetSearchGeneration) return;
                    page = rows.Select(_winget.FromIndex).ToList();
                    total = t;
                    canPage = true;
                    source = "official winget index";
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { problems.Add($"official index search failed: {ex.Message}"); }
            }

            // 3. api.winget.run (community mirror) for searches, then the built-in list.
            if (page is null && query.Length > 0 && !append)
            {
                try
                {
                    page = await _winget.SearchWingetRunAsync(query, cts.Token);
                    if (gen != _wingetSearchGeneration) return;
                    total = page.Count;
                    source = "api.winget.run (community mirror, data frozen since 2023)";
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (Exception ex) { problems.Add($"api.winget.run: {Reason(ex)}"); }
            }

            if (page is null)
            {
                if (append) return;
                page = _winget.BuiltIn.Where(a =>
                        (category is null || a.Category == category.Label || (category.Slug == "development" && a.Category == "Developer")) &&
                        (query.Length == 0 ||
                         a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         a.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         a.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                total = page.Count;
                source = BuiltInSourceLabel;
            }

            if (gen != _wingetSearchGeneration) return;
            if (!append)
            {
                _appsResults.Clear();
                AppsV.Scroll.ScrollToTop();
            }
            foreach (var item in page)
            {
                if (_appsResults.Contains(item)) continue;
                _appsResults.Add(item);
                _wingetIcons.Attach(item);
            }
            _appsOffset = offset + page.Count;
            _appsTotal = Math.Max(total, _appsResults.Count);
            _appsCanPage = canPage && page.Count > 0;
            AppsV.LoadMoreBtn.Visibility = _appsCanPage && _appsOffset < _appsTotal ? Visibility.Visible : Visibility.Collapsed;
            AppsV.EmptyText.Visibility = _appsResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            AppsV.TitleText.Text = query.Length > 0 ? $"Results for “{query}” ({_appsTotal:N0})"
                : category is not null ? $"{category.Label} ({_appsTotal:N0})"
                : $"All apps ({_appsTotal:N0})";
            if (query.Length == 0 && category is null && source.StartsWith("api.winstall", StringComparison.Ordinal) && !_wingetIndex.IsAvailable)
                DiscoverV.PackageCountText.Text = _appsTotal.ToString("N0");

            var summary = $"Showing {_appsResults.Count:N0} of {_appsTotal:N0} · source: {source}";
            if (problems.Count > 0)
            {
                summary += " · " + string.Join("; ", problems);
                foreach (var p in problems) DeployLog("catalog", null, p);
            }
            AppsV.ResultText.Text = summary;

            // Keep only what is visible, selected, busy or built-in (search results never accumulate).
            if (!append) _winget.Prune(_appsResults.Concat(_selectedApps).Concat(_trendingApps).Concat(PackPreviewItems()));
        }
        finally
        {
            if (gen == _wingetSearchGeneration) _appsLoading = false;
        }
    }

    private IEnumerable<WingetAppItem> PackPreviewItems() =>
        _discoverPacks.Concat(_featuredPacks).Concat(_communityPacks).SelectMany(p => p.Apps);

    // ---------------- App page ----------------
    private void OpenAppPage(WingetAppItem item)
    {
        _wingetIcons.Attach(item);
        DetailV.ShowItem(item);
        DeployShell.Show(DeployView.Detail);
        _detailTask = LoadAppDetailsAsync(item, toPage: true);
    }

    /// <summary>Fetches winstall details (versions, licence, homepage...) into the item; index-only data when offline.</summary>
    private async Task LoadAppDetailsAsync(WingetAppItem item, bool toPage)
    {
        await _detailsGate.WaitAsync();
        try
        {
            var (d, stale) = await _winstall.GetAppAsync(item.Id);
            item.SetVersions(d.Versions, d.LatestVersion);
            if (!item.IsBuiltIn && d.Description.Length > 0) item.Description = d.Description;
            if (d.LatestVersion.Length > 0 && !item.IsBuiltIn) item.Version = d.LatestVersion;
            item.DetailsLoaded = true;
            if (toPage) DetailV.SetDetails(item, d, stale ? "Details from the offline cache (api.winstall.app is unreachable)." : null);
        }
        catch (Exception ex)
        {
            item.EnsureVersionOptions();
            var note = ex is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                ? "winstall has no page for this package; showing what the winget index knows."
                : $"Details unavailable ({Reason(ex)}); showing what the winget index knows.";
            if (toPage) DetailV.SetDetails(item, null, note);
        }
        finally
        {
            _detailsGate.Release();
        }
    }

    // ---------------- Selection ----------------
    private void UpdateWingetSelectedCount()
    {
        var n = _selectedApps.Count;
        InstallV.SelectedTitle.Text = $"Selected apps ({n})";
        InstallV.SelectedEmptyText.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallV.QueueEmptyText.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallV.DeployBtnText.Text = n == 0 ? "Deploy" : $"Deploy {n} app{(n == 1 ? "" : "s")}";
        PacksV.NewPackHint.Text = $"Saves the {n} selected app{(n == 1 ? "" : "s")} under a name (stored in winget-packs.json). Saving under an existing custom pack name replaces it.";
        RegenerateScript(); // MainWindow.DeployRun.cs
    }

    private void SetQueued(WingetAppItem item, bool queued)
    {
        if (queued)
        {
            item.IsSelected = true;
            if (!_selectedApps.Contains(item))
            {
                _selectedApps.Add(item);
                _wingetIcons.Attach(item);
                item.EnsureVersionOptions();
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
        _selectedApps.Remove(item);
    }

    private void OnClearWingetQueueClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in _selectedApps.ToList())
            SetQueued(item, false);
    }

    /// <summary>Selects validated IDs, resolving names/versions from the index when possible.</summary>
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

            if (!_selectedApps.Contains(item)) added++;
            SetQueued(item, true);
        }
        return (added, notInIndex);
    }

    // ---------------- Packs ----------------
    private WingetAppItem ResolvePreview(string id, IReadOnlyDictionary<string, IndexPackage> found) =>
        _winget.Find(id) ?? (found.TryGetValue(id, out var p) ? _winget.FromIndex(p)
            : _winget.FromPackApp(new WinstallPackApp(id, id.Contains('.') ? id[(id.IndexOf('.') + 1)..] : id, "", null)));

    private PackCard CardFromPack(WingetPack pack)
    {
        Dictionary<string, IndexPackage> found = new(StringComparer.OrdinalIgnoreCase);
        var preview = pack.PackageIds.Take(5).ToList();
        if (_wingetIndex.IsAvailable)
        {
            try { found = _wingetIndex.Lookup(preview.Where(id => _winget.Find(id) is null)); } catch { }
        }
        var apps = preview.Select(id => ResolvePreview(id, found)).ToList();
        foreach (var a in apps) _wingetIcons.Attach(a);
        return new PackCard
        {
            Title = pack.Name,
            Description = pack.Description,
            Apps = apps,
            AppCount = pack.PackageIds.Count,
            PackageIds = pack.PackageIds,
            Pack = pack,
            Badge = pack.IsCustom ? "Custom" : "Preset",
        };
    }

    private PackCard CardFromWinstall(WinstallPack p)
    {
        var apps = p.Apps.Take(5).Select(_winget.FromPackApp).ToList();
        foreach (var a in apps) _wingetIcons.Attach(a);
        return new PackCard
        {
            Title = p.Name,
            Description = p.Description,
            Apps = apps,
            AppCount = p.AppCount,
            WinstallId = p.Id,
            Badge = $"{p.AppCount} apps",
        };
    }

    private void RefreshPackLists()
    {
        PacksV.CustomPacksList.ItemsSource = _winget.CustomPacks.Select(CardFromPack).ToList();
        PacksV.CustomEmpty.Visibility = _winget.CustomPacks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PacksV.PresetPacksList.ItemsSource = _winget.Presets.Select(CardFromPack).ToList();
        BarV.SetCustomPacks(_winget.CustomPacks);
    }

    private async Task LoadPacksAsync()
    {
        try
        {
            var page = await _winstall.TrendingPacksAsync();
            _featuredPacks.Clear();
            foreach (var p in page.Items) _featuredPacks.Add(CardFromWinstall(p));
            PacksV.FeaturedSubtitle.Text = page.Stale ? "Trending public packs on winstall.app (offline cache)." : "Trending public packs on winstall.app.";
        }
        catch (Exception ex)
        {
            PacksV.FeaturedSubtitle.Text = $"winstall packs are unavailable ({Reason(ex)}). Presets and your packs still work.";
        }
        await LoadCommunityPacksAsync(append: false);
    }

    private async Task LoadCommunityPacksAsync(bool append)
    {
        var offset = append ? _communityOffset : 0;
        try
        {
            var page = await _winstall.ListPacksAsync(offset, CommunityPageSize);
            if (!append) _communityPacks.Clear();
            var featuredIds = _featuredPacks.Select(f => f.WinstallId).ToHashSet();
            foreach (var p in page.Items.Where(p => !featuredIds.Contains(p.Id) && p.AppCount > 0))
                _communityPacks.Add(CardFromWinstall(p));
            _communityOffset = offset + page.Items.Count;
            _communityTotal = page.Total;
            PacksV.CommunityTitle.Text = $"Community packs ({_communityTotal:N0})";
            PacksV.LoadMoreBtn.Visibility = _communityOffset < _communityTotal && page.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            PacksV.CommunityTitle.Text = $"Community packs (unavailable: {Reason(ex)})";
            PacksV.LoadMoreBtn.Visibility = Visibility.Collapsed;
        }
    }

    private async Task SelectPackAsync(PackCard card)
    {
        try
        {
            PackImportResult? info = null;
            IReadOnlyList<string> ids;
            if (card.PackageIds is not null)
            {
                ids = card.PackageIds;
            }
            else if (card.WinstallId is not null)
            {
                SetPackStatus($"Fetching winstall pack '{card.Title}'...");
                info = await WinstallImporter.FetchPackAsync(card.WinstallId);
                ids = info.Ids;
            }
            else return;

            var (added, notInIndex) = AddIdsToQueue(ids, info);
            var msg = $"Pack '{card.Title}': {ids.Count} apps, {added} added to the selection.";
            if (notInIndex.Count > 0) msg += $" Not in the official index: {string.Join(", ", notInIndex)}.";
            DeployLog("packs", null, msg);
            SetPackStatus(msg);
        }
        catch (Exception ex)
        {
            var msg = $"Could not load pack '{card.Title}': {Reason(ex)}";
            DeployLog("packs", null, msg);
            SetPackStatus(msg);
        }
    }

    private void SetPackStatus(string msg)
    {
        StatusText.Text = msg;
        PacksV.StatusText.Text = msg;
        PacksV.StatusText.Visibility = Visibility.Visible;
    }

    private void ToggleNewPackPanel(bool show)
    {
        PacksV.NewPackPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { PacksV.ImportPanel.Visibility = Visibility.Collapsed; PacksV.PackNameBox.Focus(); }
    }

    private void SaveSelectionAsPack(string rawName, TextBlock result)
    {
        var name = rawName.Trim();
        void Report(string msg) { result.Text = msg; result.Visibility = Visibility.Visible; StatusText.Text = msg; }
        if (name.Length == 0) { Report("Enter a pack name first."); return; }
        if (_winget.Presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Report($"'{name}' is a built-in preset name; choose another name.");
            return;
        }
        try
        {
            var replaced = _winget.CustomPacks.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var pack = _winget.SaveCustomPack(name, _selectedApps.Select(a => a.Id).ToList());
            RefreshPackLists();
            var msg = $"{(replaced ? "Updated" : "Saved")} custom pack '{pack.Name}' with {pack.PackageIds.Count} apps.";
            DeployLog("packs", null, msg);
            Report(msg);
        }
        catch (Exception ex)
        {
            Report($"Could not save pack: {ex.Message}");
            DeployLog("packs", null, $"Save failed: {ex.Message}");
        }
    }

    private void AddSelectionToPack(WingetPack pack)
    {
        try
        {
            var ids = pack.PackageIds.Concat(_selectedApps.Select(a => a.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var saved = _winget.SaveCustomPack(pack.Name, ids);
            RefreshPackLists();
            var msg = $"Pack '{saved.Name}' now has {saved.PackageIds.Count} apps.";
            BarV.PackResultText.Text = msg;
            StatusText.Text = msg;
            DeployLog("packs", null, msg);
        }
        catch (Exception ex)
        {
            BarV.PackResultText.Text = $"Could not update the pack: {ex.Message}";
        }
    }

    private void DeleteCustomPack(PackCard card)
    {
        if (card.Pack is not { IsCustom: true } pack) return;
        if (MessageBox.Show(this, $"Delete the custom pack '{pack.Name}'?", "Home RMS - Packs",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            _winget.DeleteCustomPack(pack.Name);
            RefreshPackLists();
            SetPackStatus($"Deleted custom pack '{pack.Name}'.");
            DeployLog("packs", null, $"Deleted custom pack '{pack.Name}'.");
        }
        catch (Exception ex)
        {
            SetPackStatus($"Could not delete pack: {ex.Message}");
            DeployLog("packs", null, $"Delete failed: {ex.Message}");
        }
    }

    // ---------------- Import ----------------
    private void OnToggleWingetImportClick(object sender, RoutedEventArgs e)
    {
        var show = PacksV.ImportPanel.Visibility != Visibility.Visible;
        PacksV.ImportPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { PacksV.NewPackPanel.Visibility = Visibility.Collapsed; PacksV.ImportBox.Focus(); }
    }

    private void OnPasteWingetImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                PacksV.ImportResultText.Text = "The clipboard has no text.";
                return;
            }
            PacksV.ImportBox.Text = text;
        }
        catch (Exception ex)
        {
            PacksV.ImportResultText.Text = $"Could not read the clipboard: {ex.Message}";
        }
    }

    private async void OnRunWingetImportClick(object sender, RoutedEventArgs e)
    {
        var text = PacksV.ImportBox.Text.Trim();
        if (text.Length == 0)
        {
            PacksV.ImportResultText.Text = "Paste a winstall pack URL, a winget script, JSON or a list of IDs first.";
            return;
        }

        PackImportResult result;
        PacksV.ImportRunBtn.IsEnabled = false;
        try
        {
            if (WinstallImporter.TryGetPackId(text, out var packId))
            {
                PacksV.ImportResultText.Text = $"Fetching winstall pack {packId}...";
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
            PacksV.ImportResultText.Text = $"Import failed: {msg}";
            DeployLog("import", null, $"Import failed: {msg}");
            return;
        }
        finally
        {
            PacksV.ImportRunBtn.IsEnabled = true;
        }

        if (result.Ids.Count == 0 && result.Rejected.Count == 0)
        {
            PacksV.ImportResultText.Text = $"No package IDs found ({result.Format}). Supported: winstall pack URL, 'winget install --id=X -e' scripts, winget export JSON, {{\"apps\":[{{\"id\":...}}]}} JSON, one ID per line.";
            return;
        }

        var (added, notInIndex) = AddIdsToQueue(result.Ids, result);
        var parts = new List<string>
        {
            $"{result.Format}{(string.IsNullOrWhiteSpace(result.PackName) ? "" : $" '{result.PackName}'")}: {result.Ids.Count} valid ID(s), {added} added to the selection.",
        };
        if (notInIndex.Count > 0)
            parts.Add($"Not in the official index (selected anyway): {string.Join(", ", notInIndex)}.");
        if (result.Rejected.Count > 0)
            parts.Add($"Rejected (not valid winget IDs): {string.Join(", ", result.Rejected)}.");
        var summary = string.Join(" ", parts);
        PacksV.ImportResultText.Text = summary;
        DeployLog("import", null, summary);

        if (!string.IsNullOrWhiteSpace(result.PackName) && string.IsNullOrWhiteSpace(PacksV.PackNameBox.Text))
            PacksV.PackNameBox.Text = result.PackName!.Length > 60 ? result.PackName[..60] : result.PackName;
    }

    private void CopyText(string text, Action onCopied)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            onCopied();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not copy to the clipboard: {ex.Message}";
        }
    }
}
