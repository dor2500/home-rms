using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Controller.Themes;
using HomeRms.Shared;
using HomeRms.Shared.Network;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

namespace HomeRms.Controller;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MachineProfile> _machines = new();
    private System.Windows.Data.ListCollectionView? _fleetView;
    private readonly ObservableCollection<FleetActivityEvent> _activityEvents = new();
    // Overview trend: one sample per probe cycle (average RTT / CPU of the online endpoints). Starts
    // empty: the chart shows an empty state instead of made-up numbers until real samples exist.
    private const int TrendSamples = 30;
    private readonly List<int> _latencyHistory = new();
    private readonly List<double> _cpuHistory = new();
    private readonly CentralHubService _centralHub;
    private bool _sidebarCollapsed;
    private MachineProfile? _connectedProfile;
    private RmsConnection? _conn;
    private AgentInfo? _agentInfo;

    // Cached inventory for instant client-side filtering
    private List<AppRow> _allApps = new();
    private List<ProcRow> _allProcs = new();

    // Shell state & history
    private AsyncDuplexStreamingCall<ShellInput, ShellOutput>? _shellCall;
    private readonly SemaphoreSlim _shellWriteLock = new(1, 1);
    private readonly List<string> _shellHistory = new();
    private int _historyIndex = -1;

    // Screen + input state
    private CancellationTokenSource? _streamCts;
    private AsyncClientStreamingCall<InputEvent, InputAck>? _inputCall;
    private Channel<InputEvent>? _inputChannel;
    private int _frameWidth = 1920;
    private int _frameHeight = 1080;
    private bool _controlWanted;
    private bool _isFullScreen;
    private WindowState _prevWindowState = WindowState.Normal;

    private sealed record AppRow(string Name, string Version, string Publisher);
    private sealed record ProcRow(int Pid, string Name, long MemoryMb);

    public MainWindow()
    {
        InitializeComponent();
        InitStreamPresets();

        // Ensure certs exist so probing/connecting works out of the box in dev/home setups
        try { DevCertificates.EnsureDeploymentCertificates(RmsPaths.CertDir, RmsPaths.CertPassword); }
        catch { /* surfaced on connect if missing */ }

        // Screenshot runs decorate the list with sample data: never write it back to machines.json.
        if (IsScreenshotRun) MachineStore.ReadOnly = true;
        foreach (var m in MachineStore.Load())
            _machines.Add(m);

        // Endpoint cards get their own view so the fleet filter never touches the Overview list.
        _fleetView = new System.Windows.Data.ListCollectionView(_machines);
        MachinesList.ItemsSource = _fleetView;
        OverviewFleetList.ItemsSource = _machines;
        OverviewEventsList.ItemsSource = _activityEvents;
        FleetActivityList.ItemsSource = _activityEvents;
        if (_machines.Count > 0)
            MachinesList.SelectedIndex = 0;

        _centralHub = new CentralHubService(() => _machines, ProbeAllMachinesAsync, AddActivity, _agentless, _ssh, _alerts);
        _centralHub.Start();
        if (HubRelayStatusText is not null)
        {
            HubRelayStatusText.Text = _centralHub.IsRunning ? $"Hub relay :{_centralHub.Port}" : "Hub standby";
            HubRelayStatusText.ToolTip = _centralHub.AuthRequired
                ? "Mobile web app relay. Sign-in required: hub access key (Server & PKI) or 2FA code."
                : "Mobile web app relay. WARNING: sign-in is turned off (Server & PKI).";
        }
        if (HubRelayDot is not null)
            HubRelayDot.Fill = (Brush)FindResource(_centralHub.IsRunning ? "OkBrush" : "WarnBrush");

        InitializeOverhaulServices();
        InitializeFeatureViews();
        InitializeEndpointLocator(); // MainWindow.Locator.cs: follow endpoints whose IP changes
        InitializeShell(); // MainWindow.Shell.cs: live console, sidebar state, nav badges, transitions
        UpdateDynamicTabVisibility(SelectedMachine);

        AddActivity("mTLS", "Central Hub & Operations Console initialized. Mutual-TLS X.509 PKI ready.");
        UpdateOverviewMetrics();
        StartInactivityTimer();

        ShellOutput.Text =
            "Home RMS Interactive PowerShell Console\r\n" +
            "Connected over Mutual TLS (HTTP/2). Every command executed here is recorded in the remote agent's visible activity log.\r\n" +
            "------------------------------------------------------------\r\n";

        DeployOutputBox.Text = FilesV.LogBox.Text =
            "Home RMS Silent Software Deployment Console\r\n" +
            "Drop .exe, .msi, .msp, .ps1, .cmd or .bat files on this page to stream them over mTLS and run them silently on the remote computer.\r\n" +
            "------------------------------------------------------------\r\n";

        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            var shotDir = GetArg(args, "--screenshot-dir");
            var portArg = GetArg(args, "--port");
            if (!string.IsNullOrWhiteSpace(shotDir))
            {
                var port = int.TryParse(portArg, out var p) ? p : RmsPaths.DefaultPort;
                // --screenshot-set deploy: only the Deploy sub-views (no agent connection needed).
                if (string.Equals(GetArg(args, "--screenshot-set"), "deploy", StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(shotDir);
                    LockOverlay.Visibility = Visibility.Collapsed;
                    AgentPair2FaOverlay.Visibility = Visibility.Collapsed;
                    await CaptureDeployScreenshotsAsync(shotDir);
                    Application.Current.Shutdown(0);
                    Environment.Exit(0);
                    return;
                }
                await RunAutomatedScreenshotsAsync(shotDir, port);
                return;
            }

            ApplyStartupLock();

            await ProbeAllMachinesAsync();
            _ = CheckGitHubUpdateInBackgroundAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = Short(ex);
        }
    }

    private AvailableUpdate? _pendingUpdate;

    private void StyleUpdateAvailable()
    {
        UpdateBtn.Background = Palette.Success;
        UpdateBtn.BorderBrush = Palette.SuccessLine;
        UpdateBtn.Foreground = Palette.TextOnAccent;
    }

    private async Task CheckGitHubUpdateInBackgroundAsync()
    {
        try
        {
            await Task.Delay(2500);
            var update = await ControllerUpdater.CheckForUpdateAsync();
            if (update is not null)
            {
                _pendingUpdate = update;
                UpdateBtn.Content = $"Update {update.Tag}";
                StyleUpdateAvailable();
                UpdateBtn.ToolTip = $"New version {update.Tag} available on GitHub. Click to update & restart.";
            }
            else
            {
                UpdateBtn.Content = $"v{ControllerUpdater.CurrentVersion.Major}.{ControllerUpdater.CurrentVersion.Minor}.{ControllerUpdater.CurrentVersion.Build}";
            }
        }
        catch { /* offline or rate-limited; ignore silently */ }
    }

    private async void OnCheckOrApplyUpdateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateBtn.IsEnabled = false;
            if (_pendingUpdate is null)
            {
                UpdateBtn.Content = "Checking…";
                _pendingUpdate = await ControllerUpdater.CheckForUpdateAsync();
                if (_pendingUpdate is null)
                {
                    UpdateBtn.Content = $"v{ControllerUpdater.CurrentVersion.Major}.{ControllerUpdater.CurrentVersion.Minor}.{ControllerUpdater.CurrentVersion.Build}";
                    MessageBox.Show($"You are running the latest version (v{ControllerUpdater.CurrentVersion}).",
                        "Home RMS — Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                UpdateBtn.Content = $"Update {_pendingUpdate.Tag}";
                StyleUpdateAvailable();
                return;
            }

            if (MessageBox.Show($"Download and install Home RMS Controller {_pendingUpdate.Tag} from GitHub now?\n\nThe app will restart automatically.",
                    "Home RMS — Update Available", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            await ControllerUpdater.DownloadAndApplyAsync(_pendingUpdate, msg => UpdateBtn.Content = msg);
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed:\n\n{ex.Message}", "Home RMS — Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateBtn.IsEnabled = true;
        }
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    // ---------------- Navigation & Sidebar ----------------
    private void OnNavTabChanged(object sender, RoutedEventArgs e)
    {
        if (OverviewPanel is null || FleetPanel is null || InventoryPanel is null || DesktopPanel is null || ShellPanel is null || DeployPanel is null || SystemPanel is null) return;

        if (_isFullScreen && NavDesktop.IsChecked != true)
            SetFullScreen(false);

        OverviewPanel.Visibility = NavOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FleetPanel.Visibility = NavFleet.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        InventoryPanel.Visibility = NavInventory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DesktopPanel.Visibility = NavDesktop.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShellPanel.Visibility = NavShell.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DeployPanel.Visibility = NavDeploy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WebUiPanel.Visibility = NavWebUi.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        var showSettings = NavUpdates.IsChecked == true || NavSystem.IsChecked == true;
        SystemPanel.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;

        if (showSettings)
        {
            if (_win11Page is null) InitWin11Settings();
            if (NavUpdates.IsChecked == true)
                SelectWin11Page(SystemCatalog.Win11Pages.FirstOrDefault(p => p.Id == "updates") ?? SystemCatalog.Win11Pages[0]);
            else if (NavSystem.IsChecked == true && _win11Page?.Id == "updates")
                SelectWin11Page(SystemCatalog.Win11Pages[0]);
        }

        UpdateFeatureViewVisibility();
        AnimateActivePanel(); // MainWindow.Shell.cs
    }

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        ApplySidebarState(animate: true); // MainWindow.Shell.cs
        _ui.SidebarCollapsed = _sidebarCollapsed;
        _ui.Save();
    }

    private void OnGoToFleetClick(object sender, RoutedEventArgs e) => NavFleet.IsChecked = true;

    private void OnOpenAboutClick(object sender, RoutedEventArgs e)
    {
        var v = ControllerUpdater.CurrentVersion;
        AboutVersionText.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        var cert = ControllerIdentity.GetOrCreateUniqueControllerCert();
        AboutFingerprintBox.Text = ControllerIdentity.GetSha256Fingerprint(cert);
        var cfg = TwoFactorStore.LoadController();
        Require2FaStartupCheck.IsChecked = cfg.RequireOnStartupOrPair;
        AllowPasskeyCheck.IsChecked = cfg.AllowPasskey;
        AboutTotpSecretBox.Text = cfg.TotpSecret;
        RememberDaysBox.Text = (cfg.RememberDays <= 0 ? 30 : cfg.RememberDays).ToString();
        AboutBackupCodesBox.Text = string.Join("   ", cfg.BackupCodes ?? []);
        var uri = TotpHelper.BuildOtpAuthUri("HomeRMS-Controller", Environment.MachineName, cfg.TotpSecret);
        var qrBytes = TotpHelper.GenerateQrPng(uri, 5);
        using (var ms = new MemoryStream(qrBytes))
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            AboutQrImage.Source = bmp;
        }
        AboutHubKeyBox.Text = _centralHub.AccessKey ?? "(unavailable)";
        AboutHubKeyLabel.Text = $"Hub relay access key (mobile web app on http://{Environment.MachineName}:{_centralHub.Port}/)";
        HubRequireAuthCheck.IsChecked = _centralHub.Settings.RequireAuth;
        AboutOverlay.Visibility = Visibility.Visible;
        _ = RefreshSecuritySectionAsync();
    }

    private void OnCopyHubKeyClick(object sender, RoutedEventArgs e)
    {
        if (_centralHub.AccessKey is not { } key) return;
        try
        {
            Clipboard.SetText(key);
            StatusText.Text = "Copied the hub relay access key to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not copy to the clipboard: {ex.Message}";
        }
    }

    private void OnRegenerateHubKeyClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Generate a new hub access key? Every phone or browser signed in to the hub relay is signed out.",
                "Home RMS - Hub relay", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        AboutHubKeyBox.Text = _centralHub.RegenerateAccessKey();
    }

    private void OnToggleHubAuth(object sender, RoutedEventArgs e)
    {
        if (AboutOverlay.Visibility != Visibility.Visible) return;
        var required = HubRequireAuthCheck.IsChecked == true;
        if (!required && MessageBox.Show(this,
                "Turn off sign-in for the hub relay?\n\nAnyone who can reach this PC on the network could then list endpoints, " +
                "run commands and view screens through the relay without a password.",
                "Home RMS - Hub relay", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            HubRequireAuthCheck.IsChecked = true;
            return;
        }
        _centralHub.SetRequireAuth(required);
    }

    private void OnToggleStartup2Fa(object sender, RoutedEventArgs e)
    {
        if (AboutOverlay.Visibility != Visibility.Visible) return;
        var cfg = TwoFactorStore.LoadController() with { RequireOnStartupOrPair = Require2FaStartupCheck.IsChecked == true };
        TwoFactorStore.SaveController(cfg);
    }

    private void OnCopyController2FaUriClick(object sender, RoutedEventArgs e)
    {
        var cfg = TwoFactorStore.LoadController();
        Clipboard.SetText(TotpHelper.BuildOtpAuthUri("HomeRMS-Controller", Environment.MachineName, cfg.TotpSecret));
        StatusText.Text = "Copied Controller 2FA Authenticator URI to clipboard.";
    }

    private void OnCopyPinCommandClick(object sender, RoutedEventArgs e)
    {
        var fp = ControllerIdentity.NormalizeFingerprint(AboutFingerprintBox.Text);
        Clipboard.SetText($"HomeRmsAgent.exe --install --silent --pin {fp}");
        StatusText.Text = "Copied Agent --pin command to clipboard.";
    }

    private void OnCloseAboutClick(object sender, RoutedEventArgs e)
    {
        ClearAboutSecretInputs();
        AboutOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnOpenProjectLinkClick(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch { }
    }

    // ---------------- System & Settings Operations ----------------
    private SystemCatalog.Win11SettingsPage? _win11Page;
    private string _win11LastArea = "system";
    private SystemCatalog.Table _sysTable = SystemCatalog.Table.Empty;
    private List<Dictionary<string, string>> _sysVisible = new();

    private void InitWin11Settings()
    {
        Win11SidebarPanel.Children.Clear();
        foreach (var page in SystemCatalog.Win11Pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock
            {
                Text = page.Icon,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                Width = 34,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = page.Title,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            var item = new Button
            {
                Content = row,
                Tag = page,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(10, 7, 10, 7),
                Style = (Style)FindResource("SubtleButton"),
                BorderThickness = new Thickness(0),
                MinHeight = 32
            };
            item.Click += (_, _) => { if (!ReferenceEquals(_win11Page, page)) SelectWin11Page(page); };
            Win11SidebarPanel.Children.Add(item);
        }
        SelectWin11Page(SystemCatalog.Win11Pages[0]);
    }

    private void SelectWin11Page(SystemCatalog.Win11SettingsPage page)
    {
        _win11Page = page;
        foreach (Button item in Win11SidebarPanel.Children)
        {
            var active = ReferenceEquals(item.Tag, page);
            item.Background = active ? Palette.AccentSoft : Brushes.Transparent;
            item.Foreground = active ? Palette.TextPrimary : Palette.TextSecondary;
            item.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (!string.IsNullOrEmpty(SettingsSearchBox.Text))
            SettingsSearchBox.Text = "";

        Win11PageTitle.Text = page.Title;
        Win11PageSubtitle.Text = page.Subtitle;
        SystemTargetLabel.Text = "Target — " + page.TargetHint;
        SystemExtraLabel.Text = "Extra — " + (page.Items.FirstOrDefault(i => i.ExtraHint is not null)?.ExtraHint ?? "(optional)");
        RenderWin11Cards(page.Items);
    }

    private void OnSettingsSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_win11Page is null) return;
        var q = SettingsSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            Win11PageTitle.Text = _win11Page.Title;
            Win11PageSubtitle.Text = _win11Page.Subtitle;
            RenderWin11Cards(_win11Page.Items);
            return;
        }

        var matches = SystemCatalog.Win11Pages
            .SelectMany(p => p.Items)
            .Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        i.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Win11PageTitle.Text = $"Search: \u201c{q}\u201d";
        Win11PageSubtitle.Text = matches.Count == 1 ? "1 matching operation" : $"{matches.Count} matching operations";
        RenderWin11Cards(matches);
    }

    private void RenderWin11Cards(IReadOnlyList<SystemCatalog.Win11SettingItem> items)
    {
        Win11CardsPanel.Children.Clear();
        foreach (var item in items)
        {
            var card = new Border
            {
                Style = (Style)FindResource("CardStyle"),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var dock = new DockPanel();
            var btn = new Button
            {
                Content = item.ButtonLabel,
                Style = (Style)FindResource(item.Dangerous ? "DangerButton" : "PrimaryButton"),
                MinWidth = 104,
                Padding = new Thickness(12, 4, 12, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (item.Dangerous) btn.ToolTip = "Modifies remote endpoint configuration — confirmation required";
            DockPanel.SetDock(btn, Dock.Right);
            btn.Click += async (_, _) => await RunWin11ItemAsync(item);

            var tagBorder = new Border
            {
                Background = Palette.AccentSoft,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = item.Area,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("AccentBrush")
                }
            };
            DockPanel.SetDock(tagBorder, Dock.Left);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            textStack.Children.Add(new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeights.SemiBold });
            var sub = new TextBlock
            {
                Text = item.Subtitle,
                FontSize = 12,
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            textStack.Children.Add(sub);

            dock.Children.Add(btn);
            dock.Children.Add(tagBorder);
            dock.Children.Add(textStack);
            card.Child = dock;
            Win11CardsPanel.Children.Add(card);
        }
    }

    private async Task RunWin11ItemAsync(SystemCatalog.Win11SettingItem item)
    {
        if (_conn is null && !await ConnectToCurrentFieldsAsync()) return;

        var target = string.IsNullOrWhiteSpace(SystemTargetBox.Text) && !string.IsNullOrWhiteSpace(item.DefaultTarget)
            ? item.DefaultTarget!
            : SystemTargetBox.Text.Trim();
        var extra = string.IsNullOrWhiteSpace(SystemExtraBox.Text) && !string.IsNullOrWhiteSpace(item.DefaultExtra)
            ? item.DefaultExtra!
            : SystemExtraBox.Text.Trim();

        if (item.NeedsTarget && target.Length == 0)
        {
            SystemMessageText.Text = $"[WARN] '{item.Title}' requires a Target ({_win11Page?.TargetHint}).";
            SystemMessageText.Foreground = ConsoleWarnBrush;
            return;
        }

        if (item.Dangerous && MessageBox.Show(
                $"{item.Title}\nTarget: {(target.Length == 0 ? "(none)" : target)}\n\nApply this on {_agentInfo?.MachineName ?? "the remote PC"}?",
                "Home RMS — Confirm Operation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _win11LastArea = item.Area;
        try
        {
            SystemMessageText.Text = $"Executing {item.Area}/{item.Action}…";
            SystemMessageText.Foreground = ConsoleMutedBrush;
            var res = await _conn!.Client.SystemOpAsync(
                new SystemOpRequest { Area = item.Area, Action = item.Action, Target = target, Extra = extra },
                deadline: DateTime.UtcNow.AddMinutes(item.LongRunning ? 50 : 2.2));

            SystemAdminText.Text = res.IsElevated ? "AGENT: ADMINISTRATOR" : "AGENT: STANDARD USER";
            SetPill(SystemAdminBadge, SystemAdminText, res.IsElevated ? Tone.Success : Tone.Caution);

            SetSystemTable(SystemCatalog.ParseTable(res.JsonData));
            SystemMessageText.Text = (res.Success ? "[OK] " : "[ERR] ") + res.Message;
            SystemMessageText.Foreground = res.Success ? ConsoleOkBrush : ConsoleErrorBrush;
        }
        catch (Exception ex)
        {
            SystemMessageText.Text = "[ERR] " + Short(ex);
            SystemMessageText.Foreground = ConsoleErrorBrush;
        }
    }

    private void SetSystemTable(SystemCatalog.Table table)
    {
        _sysTable = table;
        ApplySystemFilter();
    }

    private void ApplySystemFilter()
    {
        if (SystemRowsList is null) return;
        var q = SystemFilterBox?.Text?.Trim() ?? "";
        _sysVisible = _sysTable.Rows
            .Where(r => q.Length == 0 || r.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        SystemHeaderText.Text = _sysTable.Columns.Count == 0 ? "" : _sysTable.FormatHeader();
        SystemRowsList.ItemsSource = _sysVisible.Select(_sysTable.FormatRow).ToList();
    }

    private void OnSystemFilterChanged(object sender, TextChangedEventArgs e) => ApplySystemFilter();

    private void OnSystemRowSelected(object sender, SelectionChangedEventArgs e)
    {
        var i = SystemRowsList.SelectedIndex;
        if (i < 0 || i >= _sysVisible.Count) return;
        var target = SystemCatalog.TargetFromRow(_win11LastArea, _sysVisible[i]);
        if (target.Length > 0) SystemTargetBox.Text = target;
    }

    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private System.Windows.Threading.DispatcherTimer? _inactivityTimer;

    private void StartInactivityTimer()
    {
        _inactivityTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _inactivityTimer.Tick += (_, _) =>
        {
            if (!IsConsoleLocked &&
                DateTime.UtcNow - _lastActivityUtc >= TimeSpan.FromMinutes(15))
            {
                ShowLockOverlay("Locked after 15 minutes of inactivity.");
            }
        };
        _inactivityTimer.Start();
    }

    private void OnDashboardActivity(object sender, MouseButtonEventArgs e) => _lastActivityUtc = DateTime.UtcNow;

    private void OnLockDashboardClick(object sender, RoutedEventArgs e) => ShowLockOverlay(null);

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _lastActivityUtc = DateTime.UtcNow;
        // While locked, keyboard input may only reach the lock overlay (no shortcuts, no remote input).
        if (IsConsoleLocked)
        {
            var isAltF4 = e.Key == Key.System && e.SystemKey == Key.F4;
            if (!isAltF4 && !IsInsideLockOverlay(Keyboard.FocusedElement))
            {
                e.Handled = true;
                FocusLockInput();
            }
            return;
        }
        if (e.Key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SetConsoleOpen(ConsoleDrawer.Visibility != Visibility.Visible, persist: true);
            return;
        }
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            if (NavDesktop.IsChecked != true)
                NavDesktop.IsChecked = true;
            SetFullScreen(!_isFullScreen);
            return;
        }
        if (e.Key == Key.Escape && _isFullScreen)
        {
            e.Handled = true;
            SetFullScreen(false);
            return;
        }

        // Forward all keyboard input to the remote PC when Remote Desktop control is active
        // and the user is not typing into a toolbar TextBox (FPS / Quality).
        if (NavDesktop.IsChecked == true && _inputCall is not null &&
            Keyboard.FocusedElement is not (TextBox or PasswordBox))
        {
            OnScreenKeyDown(sender, e);
        }
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (IsConsoleLocked) return;
        if (NavDesktop.IsChecked == true && _inputCall is not null &&
            Keyboard.FocusedElement is not (TextBox or PasswordBox))
        {
            OnScreenKeyUp(sender, e);
        }
    }

    private void OnToggleFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(!_isFullScreen);

    private void SetFullScreen(bool fullScreen)
    {
        if (_isFullScreen == fullScreen) return;
        _isFullScreen = fullScreen;

        if (fullScreen)
        {
            _prevWindowState = WindowState;
            RailColumn.Width = new GridLength(0);
            RailBorder.Visibility = Visibility.Collapsed;
            TopBarBorder.Visibility = Visibility.Collapsed;
            DesktopToolbarBorder.Visibility = Visibility.Collapsed;
            SetConsoleChromeHidden(true); // MainWindow.Shell.cs
            WorkspaceGrid.Margin = new Thickness(0);
            ScreenCanvasBorder.CornerRadius = new CornerRadius(0);
            ScreenCanvasBorder.BorderThickness = new Thickness(0);
            FullscreenHud.Visibility = Visibility.Visible;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            ScreenImage.Focus();
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _prevWindowState == WindowState.Minimized ? WindowState.Normal : _prevWindowState;

            RailColumn.Width = GridLength.Auto;
            RailBorder.Visibility = Visibility.Visible;
            TopBarBorder.Visibility = Visibility.Visible;
            DesktopToolbarBorder.Visibility = Visibility.Visible;
            SetConsoleChromeHidden(false);
            WorkspaceGrid.Margin = (Thickness)FindResource("PageMargin");
            ScreenCanvasBorder.CornerRadius = (CornerRadius)FindResource("RadiusCard");
            ScreenCanvasBorder.BorderThickness = new Thickness(1);
            FullscreenHud.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- Machine List, Drawer & Probing ----------------
    private void OnOpenAddEndpointDrawerClick(object sender, RoutedEventArgs e)
    {
        DrawerTitleText.Text = "Add Managed Endpoint";
        NameBox.Text = "";
        HostBox.Text = "";
        PortBox.Text = "7443";
        MacBox.Text = "";
        SetupConnectUsing(null); // MainWindow.Locator.cs
        EndpointDrawerOverlay.Visibility = Visibility.Visible;
        NameBox.Focus();
    }

    private void OnCloseEndpointDrawerClick(object sender, RoutedEventArgs e)
        => EndpointDrawerOverlay.Visibility = Visibility.Collapsed;

    private void OnCardEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MachineProfile m }) return;
        MachinesList.SelectedItem = m;
        DrawerTitleText.Text = $"Edit {m.DisplayTitle}";
        NameBox.Text = m.Name;
        HostBox.Text = m.Host;
        PortBox.Text = m.Port.ToString();
        MacBox.Text = m.MacAddress;
        SetupConnectUsing(m); // MainWindow.Locator.cs
        EndpointDrawerOverlay.Visibility = Visibility.Visible;
    }

    private void OnFleetFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_fleetView is null) return;
        var q = FleetFilterBox?.Text?.Trim() ?? "";
        _fleetView.Filter = q.Length == 0
            ? null
            : o => o is MachineProfile m &&
                   (m.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.Host.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.OsTag.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void OnClearActivityLogClick(object sender, RoutedEventArgs e) => _activityEvents.Clear();

    private void OnMachineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MachinesList.SelectedItem is not MachineProfile m) return;
        NameBox.Text = m.Name;
        HostBox.Text = m.Host;
        PortBox.Text = m.Port.ToString();
        MacBox.Text = m.MacAddress;
        EndpointKindCombo.SelectedIndex = (int)m.Kind;
        UsernameBox.Text = m.Username;
        EndpointPasswordBox.Password = "";
        SshPortBox.Text = m.SshPort.ToString();
        SshKeyBox.Text = m.SshKeyPath;
        WebUrlBox.Text = m.WebUrl;
        UseMultimonCheck.IsChecked = m.UseRdpMultimon;
        UpdateDynamicTabVisibility(m);

        if (_conn is null)
        {
            TargetTitleText.Text = m.DisplayTitle;
            StatusText.Text = $"{m.Address}  ·  {m.ModeBadge}";
        }
    }

    private async void OnMachineDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MachinesList.SelectedItem is not MachineProfile m) return;
        await ConnectToProfileAsync(m);
    }

    private void OnSaveMachineClick(object sender, RoutedEventArgs e)
    {
        var draft = BuildProfileFromDrawer();
        if (!ValidateDrawerAddress(draft)) return; // MainWindow.Locator.cs
        if (string.IsNullOrWhiteSpace(draft.Name)) draft.Name = draft.Host;

        // Editing keeps the same profile even when its address changes (e.g. switched to the computer name).
        var existing = _editingProfile is not null && _machines.Contains(_editingProfile)
            ? _editingProfile
            : _machines.FirstOrDefault(m => string.Equals(m.Host, draft.Host, StringComparison.OrdinalIgnoreCase) && m.Port == draft.Port);

        if (existing is not null)
        {
            if (!string.Equals(existing.Host, draft.Host, StringComparison.OrdinalIgnoreCase) || existing.Port != draft.Port)
            {
                _pool.Evict(existing.DialHost, existing.Port);
                _centralHub.EvictConnection(existing.DialHost, existing.Port);
                existing.Host = draft.Host;
                existing.Port = draft.Port;
            }
            existing.Name = draft.Name;
            existing.MacAddress = draft.MacAddress;
            existing.Kind = draft.Kind;
            existing.Username = draft.Username;
            if (!string.IsNullOrEmpty(draft.EncryptedPassword))
                existing.EncryptedPassword = draft.EncryptedPassword;
            existing.SshPort = draft.SshPort;
            existing.SshKeyPath = draft.SshKeyPath;
            existing.WebUrl = draft.WebUrl;
            existing.UseRdpMultimon = draft.UseRdpMultimon;
            MachinesList.SelectedItem = existing;
            UpdateDynamicTabVisibility(existing);
            AddActivity("FLEET", $"Updated endpoint '{existing.Name}' ({existing.ModeBadge}).");
        }
        else
        {
            draft.StatusLabel = "Saved";
            _machines.Add(draft);
            MachinesList.SelectedItem = draft;
            UpdateDynamicTabVisibility(draft);
            AddActivity("FLEET", $"Added new endpoint '{draft.Name}' ({draft.ModeBadge}).");
        }

        MachineStore.Save(_machines);
        EndpointDrawerOverlay.Visibility = Visibility.Collapsed;
        UpdateOverviewMetrics();
        _ = ProbeAllMachinesAsync();
    }

    private void OnRemoveMachineClick(object sender, RoutedEventArgs e)
    {
        if (MachinesList.SelectedItem is not MachineProfile selected) return;
        if (_connectedProfile == selected) Disconnect();
        var title = selected.DisplayTitle;
        _machines.Remove(selected);
        MachineStore.Save(_machines);
        if (_machines.Count > 0) MachinesList.SelectedIndex = 0;
        EndpointDrawerOverlay.Visibility = Visibility.Collapsed;
        AddActivity("FLEET", $"Removed endpoint '{title}'.");
        UpdateOverviewMetrics();
    }

    private async void OnProbeMachinesClick(object sender, RoutedEventArgs e) => await ProbeAllMachinesAsync();

    private async Task ProbeAllMachinesAsync()
    {
        if (_scanRunning) return;
        _scanRunning = true;
        if (ProbeBtn is not null) ProbeBtn.IsEnabled = false;
        try
        {
            // Find agents on the LAN first so endpoints whose IP changed are probed at the new address.
            await RunHealAsync(addNew: true);

            var tasks = _machines.ToList().Select(ProbeOneAsync);
            await Task.WhenAll(tasks);
            MachineStore.Save(_machines); // computer names, addresses and "last seen" learned by the probes

            var onlineCount = _machines.Count(x => x.IsOnline || x.IsConnected);
            AddActivity("LAN SCAN", $"Probed {_machines.Count} endpoint(s): {onlineCount} online, {_machines.Count - onlineCount} offline.");
            RecordTrendSample();
            UpdateOverviewMetrics();
        }
        finally
        {
            _scanRunning = false;
            if (ProbeBtn is not null) ProbeBtn.IsEnabled = true;
        }
    }

    /// <summary>Probes one endpoint and updates its card state (agents: a GetAgentInfo round trip).</summary>
    private async Task ProbeOneAsync(MachineProfile m)
    {
                if (m == _connectedProfile && _conn is not null)
                {
                    m.IsOnline = true;
                    m.IsConnected = true;
                    m.StatusLabel = "Connected";
                    return;
                }

                if (m.IsAgentless)
                {
                    var res = await _agentless.ProbeAsync(m);
                    m.IsOnline = res.Ok;
                    m.IsConnected = false;
                    m.StatusLabel = res.Ok ? "WinRM Ready" : "Offline";
                    m.LatencyMs = res.LatencyMs;
                    m.CpuPercent = res.Cpu;
                    m.RamPercent = res.Ram;
                    m.OsName = res.Os;
                    m.UserAndOs = $"{res.User} • Agentless";
                    _alerts.EvaluateSample(m.DisplayTitle, m.Host, res.Ok, res.Cpu, res.Ram);
                    return;
                }

                if (m.IsAppliance)
                {
                    var res = await _ssh.ProbeAsync(m);
                    m.IsOnline = res.Online;
                    m.IsConnected = false;
                    m.StatusLabel = res.StatusText;
                    m.LatencyMs = res.LatencyMs;
                    m.CpuPercent = res.CpuPercent;
                    m.RamPercent = res.RamPercent;
                    m.UserAndOs = res.ServicesSummary;
                    _alerts.EvaluateSample(m.DisplayTitle, m.Host, res.Online, res.CpuPercent, res.RamPercent);
                    return;
                }

                try
                {
                    var sw = Stopwatch.StartNew();
                    using var temp = new RmsConnection(m.DialHost, m.Port);
                    var info = await temp.Client.GetAgentInfoAsync(
                        new Empty(), deadline: DateTime.UtcNow.AddSeconds(2.5));
                    sw.Stop();
                    m.IsOnline = true;
                    m.IsConnected = false;
                    m.StatusLabel = "Online";
                    m.LatencyMs = (int)Math.Max(1, sw.ElapsedMilliseconds);
                    m.OsName = info.OsVersion;
                    m.UserAndOs = $"{info.LoggedInUser} • {info.PrimaryScreenWidth}×{info.PrimaryScreenHeight}";
                    RecordAgentContact(m, temp, info); // computer name, IP reached, certificate, last seen (MainWindow.Locator.cs)
                    // Real CPU / RAM / disk values come from the live telemetry stream (TelemetryHub);
                    // the probe only tracks reachability for the offline alert.
                    _alerts.EvaluateSample(m.DisplayTitle, m.Host, true, m.CpuPercent, m.RamPercent);
                }
                catch (Exception ex)
                {
                    // Offline is the expected outcome for a powered-off endpoint; the reason goes to the card tooltip only.
                    Debug.WriteLine($"Probe of {m.DisplayTitle} ({m.DialHost}:{m.Port}) failed: {Short(ex)}");
                    m.IsOnline = false;
                    m.IsConnected = false;
                    m.StatusLabel = "Offline";
                    m.LatencyMs = 0;
                    _alerts.EvaluateSample(m.DisplayTitle, m.Host, false, 0, 0);
                }
    }

    // ---------------- Connection & Card Quick Actions ----------------
    private async void OnCardConnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MachineProfile m }) return;
        await ConnectToProfileAsync(m);
    }

    private async void OnCardTerminalClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MachineProfile m }) return;
        if (_connectedProfile != m || _conn is null)
        {
            if (!await ConnectToProfileAsync(m)) return;
        }
        NavShell.IsChecked = true;
        ShellInputBox.Focus();
    }

    private async void OnCardWakeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MachineProfile m }) return;
        if (PowerActions.NormalizeMac(m.MacAddress) is null)
        {
            var mac = Views.InputDialog.Prompt(this, "Wake-on-LAN",
                $"No MAC address saved for '{m.DisplayTitle}'. Enter it (e.g. 00-11-22-33-44-55):", m.MacAddress,
                v => PowerActions.NormalizeMac(v) is null ? "Enter 12 hex digits (separators optional)." : null);
            if (mac is null) return;
            m.MacAddress = PowerActions.NormalizeMac(mac)!;
            MachineStore.Save(_machines);
        }
        try
        {
            await WakeOnLan.SendAsync(m.MacAddress);
            AddActivity("WAKE", $"Sent Wake-on-LAN magic packet to {m.DisplayTitle} ({m.MacAddress}).");
            StatusText.Text = $"Sent Wake-on-LAN packet to {m.DisplayTitle}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Wake-on-LAN failed:\n\n{ex.Message}", "Home RMS — Wake-on-LAN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnMassRunClick(object sender, RoutedEventArgs e)
    {
        var cmd = MassCommandBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cmd)) return;

        // Reuses the Script Library executor: pairs each agent via GetAgentInfo first and reports
        // PERMISSION_DENIED / 2FA_REQUIRED clearly instead of failing with a raw RPC error.
        await MassRunAsync(cmd);
    }

    private async void OnCardRestartClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MachineProfile m }) return;
        if (MessageBox.Show($"Restart remote endpoint '{m.DisplayTitle}' ({m.Address})?",
                "Home RMS — Confirm Restart", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (_connectedProfile != m || _conn is null)
        {
            if (!await ConnectToProfileAsync(m)) return;
        }

        try
        {
            var res = await _conn!.Client.SystemOpAsync(
                new SystemOpRequest { Area = "power", Action = "restart", Target = "", Extra = "" },
                deadline: DateTime.UtcNow.AddSeconds(15));
            AddActivity("ALERT", $"Restart issued to {m.DisplayTitle}: {res.Message}");
        }
        catch (Exception ex)
        {
            AddActivity("ALERT", $"Restart failed for {m.DisplayTitle}: {Short(ex)}");
        }
    }

    private async Task<bool> ConnectToProfileAsync(MachineProfile m)
    {
        if (_connectedProfile == m && _conn is not null)
        {
            Disconnect();
            return false;
        }
        if (_conn is not null) Disconnect();
        MachinesList.SelectedItem = m;
        NameBox.Text = m.Name;
        HostBox.Text = m.Host;
        PortBox.Text = m.Port.ToString();
        MacBox.Text = m.MacAddress;
        return await ConnectToCurrentFieldsAsync(m);
    }

    private TaskCompletionSource<string?>? _agent2FaTcs;

    private void OnSubmitAgent2FaClick(object sender, RoutedEventArgs e)
    {
        var code = Agent2FaCodeBox.Text.Trim();
        AgentPair2FaOverlay.Visibility = Visibility.Collapsed;
        _agent2FaTcs?.TrySetResult(code);
    }

    private void OnCancelAgent2FaClick(object sender, RoutedEventArgs e)
    {
        AgentPair2FaOverlay.Visibility = Visibility.Collapsed;
        _agent2FaTcs?.TrySetResult(null);
    }

    /// <summary>
    /// Connects to <paramref name="target"/> (or, without a profile, to the Host / Port fields). When the
    /// endpoint does not answer (transport error), it is looked up again once — name resolution, its other
    /// known addresses, LAN discovery by computer name — and the connection is retried at the new address.
    /// </summary>
    private async Task<bool> ConnectToCurrentFieldsAsync(MachineProfile? target = null)
    {
        var host = target?.DialHost ?? HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host)) host = "localhost";
        var port = target?.Port ?? (int.TryParse(PortBox.Text, out var p) ? p : 7443);

        try
        {
            StatusText.Text = $"Establishing Mutual-TLS handshake with {host}:{port}…";
            SetBadge("Connecting…", Tone.Caution);

            var sw = Stopwatch.StartNew();
            var conn = new RmsConnection(host, port);
            try
            {
                try
                {
                    _agentInfo = await conn.Client.GetAgentInfoAsync(new Empty(),
                        deadline: DateTime.UtcNow.AddSeconds(8));
                }
                catch (Exception ex) when (target is not null && EndpointLocator.IsTransportError(ex))
                {
                    conn.Dispose();
                    StatusText.Text = $"{target.DisplayTitle} is not answering at {host}:{port} — looking for it on the network…";
                    if (!await RelocateProfileAsync(target, CancellationToken.None)) throw;

                    host = target.DialHost;
                    port = target.Port;
                    StatusText.Text = target.ConnectsByIp
                        ? $"{target.DisplayTitle} changed IP to {host} — reconnecting…"
                        : $"{target.DisplayTitle} found at {host} — reconnecting…";
                    sw.Restart();
                    conn = new RmsConnection(host, port);
                    _agentInfo = await conn.Client.GetAgentInfoAsync(new Empty(),
                        deadline: DateTime.UtcNow.AddSeconds(8));
                }
            }
            catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unauthenticated)
            {
                _agent2FaTcs?.TrySetResult(null); // a superseded prompt must not hang its caller
                _agent2FaTcs = new TaskCompletionSource<string?>();
                Agent2FaCodeBox.Text = "";
                AgentPairPromptText.Text = $"Enter the 6-digit 2FA code shown on {host}:{port} to pair and pin this Controller:";
                AgentPair2FaOverlay.Visibility = Visibility.Visible;
                Agent2FaCodeBox.Focus();

                var code = await _agent2FaTcs.Task;
                if (string.IsNullOrWhiteSpace(code))
                    throw new OperationCanceledException("2FA pairing cancelled.");

                var pairRes = await conn.Client.PairWith2FaAsync(
                    new PairRequest { TotpCode = code, ControllerName = Environment.MachineName },
                    deadline: DateTime.UtcNow.AddSeconds(8));
                if (!pairRes.Success)
                    throw new UnauthorizedAccessException(pairRes.Message);

                _agentInfo = await conn.Client.GetAgentInfoAsync(new Empty(),
                    deadline: DateTime.UtcNow.AddSeconds(8));
            }
            sw.Stop();
            _conn = conn;
            _frameWidth = _agentInfo.PrimaryScreenWidth;
            _frameHeight = _agentInfo.PrimaryScreenHeight;

            var rtt = (int)Math.Max(1, sw.ElapsedMilliseconds);

            var profile = target ?? _machines.FirstOrDefault(m =>
                string.Equals(m.DialHost, host, StringComparison.OrdinalIgnoreCase) && m.Port == port);
            if (profile is not null)
            {
                if (RecordAgentContact(profile, conn, _agentInfo)) MachineStore.Save(_machines);
                _connectedProfile = profile;
                profile.IsOnline = true;
                profile.IsConnected = true;
                profile.StatusLabel = "Connected";
                profile.LatencyMs = rtt;
                profile.OsName = _agentInfo.OsVersion;
                profile.UserAndOs = $"{_agentInfo.LoggedInUser} • {_agentInfo.PrimaryScreenWidth}×{_agentInfo.PrimaryScreenHeight}";
            }

            TargetTitleText.Text = _agentInfo.MachineName;
            StatusText.Text =
                $"{host}:{port}  ·  {_agentInfo.LoggedInUser}  ·  {_agentInfo.PrimaryScreenWidth}×{_agentInfo.PrimaryScreenHeight}  ·  {_agentInfo.OsVersion}";
            SetBadge($"Connected · {rtt} ms", Tone.Success);
            AddActivity("CONNECT", $"mTLS session established with {_agentInfo.MachineName} ({host}:{port}) in {rtt} ms.");

            var monCount = Math.Max(1, _agentInfo.MonitorCount);
            MonitorCombo.Items.Clear();
            for (var i = 1; i <= monCount; i++)
                MonitorCombo.Items.Add(new ComboBoxItem { Content = $"Display {i}" });
            MonitorCombo.SelectedIndex = 0;
            ApplyStreamPresetForConnection();

            await RefreshInventoryCoreAsync();
            UpdateOverviewMetrics();
            return true;
        }
        catch (Exception ex)
        {
            _conn = null;
            SetBadge("Connection failed", Tone.Critical);
            StatusText.Text = "Could not connect: " + Short(ex);
            AddActivity("ALERT", $"mTLS connection to {host}:{port} failed: {Short(ex)}");
            UpdateOverviewMetrics();
            if (!IsScreenshotRun) // a modal box would block the automated screenshot run
                MessageBox.Show($"Could not connect to {host}:{port}\n\n{ex.Message}",
                    "Home RMS — Connection Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Disconnect()
    {
        var prevName = _agentInfo?.MachineName ?? _connectedProfile?.DisplayTitle ?? "endpoint";
        StopStream();
        try { _shellCall?.Dispose(); } catch { }
        _shellCall = null;
        _conn?.Dispose();
        _conn = null;
        _agentInfo = null;

        if (_connectedProfile is not null)
        {
            _connectedProfile.IsConnected = false;
            _connectedProfile.StatusLabel = _connectedProfile.IsOnline ? "Online" : "Ready";
            _connectedProfile = null;
        }

        TargetTitleText.Text = "Fleet Operations Center";
        SetBadge("Disconnected", Tone.Neutral);
        StatusText.Text = "Session closed. Select an endpoint in Fleet to connect.";
        AddActivity("DISCONNECT", $"Closed mTLS session with {prevName}.");
        UpdateOverviewMetrics();
    }

    private void AddActivity(string category, string message)
    {
        // Badge tone comes from the semantic palette; every line is mirrored to the Live Console.
        var (source, tone) = LogBus.Classify(category);
        if (tone == PaletteTone.Neutral) tone = PaletteTone.Accent;
        var bg = Palette.Soft(tone);
        var fg = Palette.Fg(tone);
        LogBus.Publish(source, category, message, tone);

        void Append()
        {
            _activityEvents.Insert(0, new FleetActivityEvent
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Category = category,
                Message = message,
                BadgeBackground = bg,
                BadgeForeground = fg
            });
            while (_activityEvents.Count > 60)
                _activityEvents.RemoveAt(_activityEvents.Count - 1);
        }

        if (Dispatcher.CheckAccess()) Append();
        else Dispatcher.Invoke(Append);
    }

    private void UpdateOverviewMetrics()
    {
        var total = _machines.Count;
        var online = _machines.Count(m => m.IsOnline || m.IsConnected);
        var offline = Math.Max(0, total - online);

        if (KpiOnlineCount is not null) KpiOnlineCount.Text = $"{online} / {total}";
        if (KpiOfflineSub is not null) KpiOfflineSub.Text = $"{offline} offline";
        if (FleetSummaryText is not null) FleetSummaryText.Text = $"{total} endpoint(s) · {online} online · {offline} offline · Hover any card for quick actions";

        if (_conn is not null && _agentInfo is not null)
        {
            if (KpiActiveSession is not null) KpiActiveSession.Text = _agentInfo.MachineName;
            if (KpiActiveLatency is not null) KpiActiveLatency.Text = $"mTLS active · {_connectedProfile?.LatencyMs ?? 1} ms RTT";
        }
        else
        {
            if (KpiActiveSession is not null) KpiActiveSession.Text = "Standby";
            if (KpiActiveLatency is not null) KpiActiveLatency.Text = $"{online} host(s) ready for mTLS";
        }

        var onlineHosts = _machines.Where(m => m.IsOnline || m.IsConnected).ToList();
        var avgCpu = onlineHosts.Count > 0 ? onlineHosts.Average(m => m.CpuPercent) : 0;
        var avgRam = onlineHosts.Count > 0 ? onlineHosts.Average(m => m.RamPercent) : 0;
        var avgRtt = onlineHosts.Count > 0 ? (int)onlineHosts.Average(m => m.LatencyMs) : 0;

        if (KpiFleetLoad is not null) KpiFleetLoad.Text = onlineHosts.Count > 0 ? $"{avgCpu:0}% CPU · {avgRam:0}% RAM" : "--% CPU · --% RAM";
        if (KpiLoadBar is not null) KpiLoadBar.Value = avgCpu;
        if (KpiAvgLatency is not null) KpiAvgLatency.Text = onlineHosts.Count > 0 ? $"{avgRtt} ms" : "-- ms";
        if (TrafficPeakText is not null) TrafficPeakText.Text = _latencyHistory.Count > 0 ? $"Peak: {_latencyHistory.Max()} ms" : "Peak: -- ms";

        var hasTrend = _latencyHistory.Count > 1;
        if (TrafficEmptyText is not null) TrafficEmptyText.Visibility = hasTrend ? Visibility.Collapsed : Visibility.Visible;
        if (!hasTrend)
        {
            if (OverviewSparkline is not null) OverviewSparkline.Points = new PointCollection();
            if (TrafficSparkline is not null) TrafficSparkline.Points = new PointCollection();
            if (CpuSparkline is not null) CpuSparkline.Points = new PointCollection();
        }

        // Update mini & large sparkline polylines from latency history
        if (OverviewSparkline is not null && _latencyHistory.Count > 1)
        {
            var max = Math.Max(20, _latencyHistory.Max());
            var pts = new PointCollection();
            for (var i = 0; i < _latencyHistory.Count; i++)
            {
                var x = i * (100.0 / (_latencyHistory.Count - 1));
                var y = 24 - (_latencyHistory[i] / (double)max * 20);
                pts.Add(new Point(x, y));
            }
            OverviewSparkline.Points = pts;
        }
        if (TrafficSparkline is not null && _latencyHistory.Count > 1)
        {
            var max = Math.Max(20, _latencyHistory.Max());
            var pts = new PointCollection();
            for (var i = 0; i < _latencyHistory.Count; i++)
            {
                var x = i * (400.0 / (_latencyHistory.Count - 1));
                var y = 100 - (_latencyHistory[i] / (double)max * 80);
                pts.Add(new Point(x, y));
            }
            TrafficSparkline.Points = pts;
        }
        if (CpuSparkline is not null && _cpuHistory.Count > 1)
        {
            var pts = new PointCollection();
            for (var i = 0; i < _cpuHistory.Count; i++)
                pts.Add(new Point(i * (400.0 / (_cpuHistory.Count - 1)), 100 - Math.Clamp(_cpuHistory[i], 0, 100) * 0.8));
            CpuSparkline.Points = pts;
        }

        UpdateNavBadges(); // MainWindow.Shell.cs
    }

    private DateTime _lastTrendSampleUtc;

    /// <summary>
    /// Adds one trend sample (average last-measured RTT / live CPU of the online endpoints); nothing when
    /// all are offline. Called after every probe and, at most every 10 s, from live telemetry.
    /// </summary>
    private void RecordTrendSample(bool throttled = false)
    {
        if (throttled && DateTime.UtcNow - _lastTrendSampleUtc < TimeSpan.FromSeconds(10)) return;
        _lastTrendSampleUtc = DateTime.UtcNow;
        var online = _machines.Where(m => (m.IsOnline || m.IsConnected) && m.LatencyMs > 0).ToList();
        if (online.Count == 0) return;
        _latencyHistory.Add((int)Math.Round(online.Average(m => m.LatencyMs)));
        _cpuHistory.Add(online.Average(m => m.CpuPercent));
        if (_latencyHistory.Count > TrendSamples) _latencyHistory.RemoveAt(0);
        if (_cpuHistory.Count > TrendSamples) _cpuHistory.RemoveAt(0);
    }

    private enum Tone { Neutral, Success, Caution, Critical }

    // Status text colours on the sunken console surfaces (semantic tokens, see Themes\Palette.cs)
    private static Brush ConsoleMutedBrush => Palette.TextSecondary;
    private static Brush ConsoleOkBrush => Palette.Success;
    private static Brush ConsoleWarnBrush => Palette.Warning;
    private static Brush ConsoleErrorBrush => Palette.Danger;

    private static void SetPill(Border pill, TextBlock text, Tone tone)
    {
        var t = tone switch
        {
            Tone.Success => PaletteTone.Success,
            Tone.Caution => PaletteTone.Warning,
            Tone.Critical => PaletteTone.Danger,
            _ => PaletteTone.Neutral
        };
        pill.Background = Palette.Soft(t);
        pill.BorderBrush = Palette.Line(t);
        text.Foreground = Palette.Fg(t);
    }

    private void SetBadge(string text, Tone tone)
    {
        ConnBadgeText.Text = text;
        SetPill(ConnBadgeBorder, ConnBadgeText, tone);
    }

    // ---------------- Inventory ----------------
    private async void OnRefreshInventory(object sender, RoutedEventArgs e)
    {
        if (_conn is null)
        {
            await ConnectToCurrentFieldsAsync();
            return;
        }
        await RefreshInventoryCoreAsync();
    }

    private async Task RefreshInventoryCoreAsync()
    {
        if (_conn is null) return;
        try
        {
            RefreshInvBtn.IsEnabled = false;
            var inv = await _conn.Client.GetInventoryAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(30));
            var hw = inv.Hardware;

            CpuValueText.Text = string.IsNullOrWhiteSpace(hw.Cpu) ? "Unknown Processor" : hw.Cpu;
            CpuSubText.Text = $"{hw.LogicalCores} logical cores active";

            RamValueText.Text = $"{Gb(hw.TotalRamBytes)} GB RAM";
            RamSubText.Text = $"{inv.Processes.Count} running processes";

            GpuValueText.Text = string.IsNullOrWhiteSpace(hw.Gpu) ? "Standard Display Adapter" : hw.Gpu;
            GpuSubText.Text = _agentInfo is not null
                ? $"Primary display: {_agentInfo.PrimaryScreenWidth}×{_agentInfo.PrimaryScreenHeight}"
                : "Primary display ready";

            if (hw.Disks.Count > 0)
            {
                var first = hw.Disks[0];
                DiskValueText.Text = $"{first.Name}  {Gb(first.FreeBytes)} GB free / {Gb(first.TotalBytes)} GB";
                DiskSubText.Text = hw.Disks.Count > 1
                    ? string.Join("  •  ", hw.Disks.Skip(1).Select(d => $"{d.Name} {Gb(d.FreeBytes)}/{Gb(d.TotalBytes)} GB"))
                    : "1 local storage volume";
            }
            else
            {
                DiskValueText.Text = "No fixed disks";
                DiskSubText.Text = "";
            }

            InventoryTimestampText.Text = $"Updated {DateTime.Now:HH:mm:ss}";

            _allApps = inv.Apps
                .OrderBy(a => a.Name)
                .Select(a => new AppRow(a.Name, a.Version, a.Publisher))
                .ToList();

            _allProcs = inv.Processes
                .OrderByDescending(p => p.WorkingSetBytes)
                .Select(p => new ProcRow(p.Pid, p.Name, p.WorkingSetBytes / (1024 * 1024)))
                .ToList();

            ApplyInventoryFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Inventory refresh failed: " + Short(ex);
        }
        finally
        {
            RefreshInvBtn.IsEnabled = true;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyInventoryFilter();

    private void ApplyInventoryFilter()
    {
        // ICollectionView filtering: the user's column sort survives both filtering and refreshes.
        var q = SearchBox?.Text?.Trim() ?? "";
        var apps = BindPreservingSort(AppsGrid, _allApps, q.Length == 0 ? null : o => o is AppRow a &&
            (a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)));
        var procs = BindPreservingSort(ProcGrid, _allProcs, q.Length == 0 ? null : o => o is ProcRow p &&
            (p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             p.Pid.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)));

        AppsCountText.Text = q.Length == 0 ? $"{_allApps.Count} apps" : $"{apps} / {_allApps.Count} apps";
        ProcsCountText.Text = q.Length == 0 ? $"{_allProcs.Count} processes" : $"{procs} / {_allProcs.Count} procs";
    }

    private async void OnKillSelectedProcessClick(object sender, RoutedEventArgs e)
    {
        if (ProcGrid.SelectedItem is not ProcRow selected)
        {
            MessageBox.Show("Select a process in the Running Processes table first.",
                "Home RMS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_conn is null && !await ConnectToCurrentFieldsAsync())
            return;

        if (MessageBox.Show($"Terminate process '{selected.Name}' (PID {selected.Pid}) on {_agentInfo?.MachineName ?? "remote PC"}?",
                "Home RMS — Confirm Process Termination", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var res = await _conn!.Client.KillProcessAsync(new KillProcessRequest { Pid = selected.Pid },
                deadline: DateTime.UtcNow.AddSeconds(10));
            StatusText.Text = res.Message;
            if (res.Success)
                await RefreshInventoryCoreAsync();
            else
                MessageBox.Show(res.Message, "Home RMS — Could Not Terminate Process", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Process termination failed: " + Short(ex);
        }
    }

    // ---------------- Shell ----------------
    private async Task EnsureShellAsync()
    {
        if (_shellCall is not null || _conn is null) return;
        _shellCall = _conn.Client.Shell();
        _ = PumpShellOutputAsync(_shellCall);
        await Task.CompletedTask;
    }

    private async Task PumpShellOutputAsync(AsyncDuplexStreamingCall<ShellInput, ShellOutput> call)
    {
        try
        {
            await foreach (var msg in call.ResponseStream.ReadAllAsync())
            {
                var text = msg.Data;
                Dispatcher.Invoke(() =>
                {
                    ShellOutput.AppendText(text);
                    ShellOutput.ScrollToEnd();
                });
            }
        }
        catch (Exception ex) when (ex is RpcException or ObjectDisposedException)
        {
            Dispatcher.Invoke(() => ShellOutput.AppendText($"\r\n[shell stream closed: {Short(ex)}]\r\n"));
        }
    }

    private async void OnShellSend(object sender, RoutedEventArgs e) => await SendShellLineAsync();

    private async void OnQuickCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string cmd })
        {
            ShellInputBox.Text = cmd;
            await SendShellLineAsync();
        }
    }

    private void OnClearShellClick(object sender, RoutedEventArgs e) => ShellOutput.Clear();

    private async void OnShellInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendShellLineAsync();
        }
        else if (e.Key == Key.Up && _shellHistory.Count > 0)
        {
            e.Handled = true;
            _historyIndex = Math.Max(0, _historyIndex <= 0 ? _shellHistory.Count - 1 : _historyIndex - 1);
            ShellInputBox.Text = _shellHistory[_historyIndex];
            ShellInputBox.CaretIndex = ShellInputBox.Text.Length;
        }
        else if (e.Key == Key.Down && _shellHistory.Count > 0)
        {
            e.Handled = true;
            _historyIndex = Math.Min(_shellHistory.Count - 1, _historyIndex + 1);
            ShellInputBox.Text = _shellHistory[_historyIndex];
            ShellInputBox.CaretIndex = ShellInputBox.Text.Length;
        }
    }

    private async Task SendShellLineAsync()
    {
        var line = ShellInputBox.Text.Trim();
        if (string.IsNullOrEmpty(line)) return;

        if (SelectedMachine is { IsAgentless: true } agentlessHost)
        {
            _shellHistory.Add(line);
            _historyIndex = _shellHistory.Count;
            ShellInputBox.Clear();
            ShellOutput.AppendText($"\r\n[WinRM] {agentlessHost.Host}> {line}\r\n");
            var (_, outText) = await _agentless.RunShellAsync(agentlessHost, line);
            ShellOutput.AppendText(outText + "\r\n");
            ShellOutput.ScrollToEnd();
            return;
        }

        if (SelectedMachine is { IsAppliance: true } sshHost)
        {
            _shellHistory.Add(line);
            _historyIndex = _shellHistory.Count;
            ShellInputBox.Clear();
            ShellOutput.AppendText($"\r\n[SSH] {sshHost.Username}@{sshHost.Host}> {line}\r\n");
            var (_, outText) = await _ssh.ExecuteSshAsync(sshHost, line);
            ShellOutput.AppendText(outText + "\r\n");
            ShellOutput.ScrollToEnd();
            return;
        }

        if (_conn is null)
        {
            if (!await ConnectToCurrentFieldsAsync()) return;
        }

        _shellHistory.Add(line);
        _historyIndex = _shellHistory.Count;
        ShellInputBox.Clear();

        await EnsureShellAsync();
        if (_shellCall is null) return;

        ShellOutput.AppendText($"\r\nPS {_agentInfo?.MachineName}> {line}\r\n");
        ShellOutput.ScrollToEnd();

        await _shellWriteLock.WaitAsync();
        try { await _shellCall.RequestStream.WriteAsync(new ShellInput { Data = line }); }
        catch (Exception ex) { ShellOutput.AppendText($"[send failed: {Short(ex)}]\r\n"); }
        finally { _shellWriteLock.Release(); }
    }

    // ---------------- Remote Desktop ----------------
    private async void OnToggleStream(object sender, RoutedEventArgs e)
    {
        if (_streamCts is not null) { StopStream(); return; }
        if (_conn is null)
        {
            if (!await ConnectToCurrentFieldsAsync()) return;
        }
        await StartStreamCoreAsync();
    }

    private async Task StartStreamCoreAsync()
    {
        if (_conn is null || _streamCts is not null) return;

        var cts = new CancellationTokenSource();
        _streamCts = cts;
        var token = cts.Token;
        var generation = ++_streamGeneration; // updates of earlier streams are dropped unpainted
        StreamBtn.Content = "Stop viewing";
        StreamBtn.Style = (Style)FindResource("DangerButton");
        StreamStatus.Text = "Awaiting on-machine consent…";

        var preset = _streamPreset;
        var monitorIndex = _selectedMonitorIndex == -1 ? -1 : Math.Max(0, MonitorCombo?.SelectedIndex ?? 0);
        var request = StreamPresets.ToRequest(CurrentStreamSettings(), Environment.UserName, monitorIndex);

        var stats = new StreamStats { PresetLabel = StreamPresets.ShortLabel(preset) };
        var statsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statsTimer.Tick += (_, _) => UpdateStreamStatus(stats);
        statsTimer.Start();

        _controlWanted = ControlCheck.IsChecked == true;
        var client = _conn.Client;
        AddActivity("SCREEN", $"Streaming {_agentInfo?.MachineName} with {StreamPresets.Label(preset)} " +
                              $"({request.Fps} fps, JPEG {request.Quality}{(request.LosslessRefine ? ", lossless refine" : "")}).");

        try
        {
            // Read + decode on a worker thread; only painting happens on the UI thread
            await Task.Run(async () =>
            {
            using var call = client.StreamScreen(request, cancellationToken: token);

            await foreach (var frame in call.ResponseStream.ReadAllAsync(token))
            {
                if (_inputCall is null && Volatile.Read(ref _controlWanted))
                    Dispatcher.Invoke(StartInputStream);

                // Decode off the UI thread (tiles in parallel), then hand over to the painter
                var update = ScreenUpdate.Decode(frame, generation);
                stats.Add(frame.CalculateSize(), update.Regions.Count > 0, frame.Width, frame.Height);
                QueuePaint(update);

                // The painter coalesces whatever piled up (latest frame wins). If it is still far behind,
                // slow the network read down: HTTP/2 back-pressure then reaches the agent.
                while (PendingPaintCount > 6 && !token.IsCancellationRequested)
                    await Task.Delay(1, token);
            }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            Dispatcher.Invoke(() => { StreamStatus.Text = "Access declined on the remote computer."; });
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                Dispatcher.Invoke(() => { StreamStatus.Text = "Stream ended: " + Short(ex); });
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                statsTimer.Stop();
                // A preset / monitor switch may already have started the next stream: leave that one alone
                if (ReferenceEquals(_streamCts, cts)) StopStream();
            });
            cts.Dispose();
        }
    }

    private async void OnMonitorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_streamCts is not null && _conn is not null)
            await RestartStreamAsync("Switching display…");
    }

    private async void OnPrivacyModeChanged(object sender, RoutedEventArgs e)
    {
        if (_conn is null) return;
        try
        {
            var enabled = PrivacyCheck.IsChecked == true;
            await _conn.Client.SetPrivacyModeAsync(
                new PrivacyModeRequest { Enabled = enabled, BannerText = "System under maintenance — Home RMS" },
                deadline: DateTime.UtcNow.AddSeconds(5));
            AddActivity("PRIVACY", $"Privacy Mode {(enabled ? "ENABLED" : "DISABLED")} on {_agentInfo?.MachineName}.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Privacy mode failed: " + Short(ex);
        }
    }

    private void OnControlCheckChanged(object sender, RoutedEventArgs e)
    {
        _controlWanted = ControlCheck.IsChecked == true;
        if (_streamCts is null) return;
        if (ControlCheck.IsChecked == true) StartInputStream();
        else StopInputStream();
    }

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts = null;
        StopInputStream();
        ResetRemoteCursor();
        if (StreamBtn is not null)
        {
            StreamBtn.Content = "Start viewing";
            StreamBtn.Style = (Style)FindResource("PrimaryButton");
        }
        if (StreamStatus is not null && !StreamStatus.Text.StartsWith("Access declined") &&
            !StreamStatus.Text.StartsWith("Stream ended"))
            StreamStatus.Text = "Idle — not streaming";
    }


    // ---------------- Input Injection ----------------
    private void StartInputStream()
    {
        if (_conn is null || _inputCall is not null) return;
        _inputCall = _conn.Client.SendInput();
        _inputChannel = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions { SingleReader = true });
        _ = PumpInputAsync(_inputCall, _inputChannel);
        ScreenImage.Focus();
        UpdateRemoteCursor();
        StartClipboardSync();
    }

    private static async Task PumpInputAsync(
        AsyncClientStreamingCall<InputEvent, InputAck> call, Channel<InputEvent> channel)
    {
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync())
                await call.RequestStream.WriteAsync(ev);
        }
        catch { /* stream ended */ }
    }

    private void StopInputStream()
    {
        _inputChannel?.Writer.TryComplete();
        _inputChannel = null;
        var call = _inputCall;
        _inputCall = null;
        if (call is not null)
            _ = Task.Run(async () => { try { await call.RequestStream.CompleteAsync(); await call; } catch { } });
        StopClipboardSync();
        UpdateRemoteCursor();
    }

    private void QueueInput(InputEvent ev) => _inputChannel?.Writer.TryWrite(ev);

    private bool TryMapToScreen(Point p, out int nx, out int ny)
    {
        nx = ny = 0;
        var cw = ScreenImage.ActualWidth;
        var ch = ScreenImage.ActualHeight;
        if (cw <= 0 || ch <= 0) return false;

        var scale = Math.Min(cw / _frameWidth, ch / _frameHeight);
        var contentW = _frameWidth * scale;
        var contentH = _frameHeight * scale;
        var offX = (cw - contentW) / 2;
        var offY = (ch - contentH) / 2;

        var fx = (p.X - offX) / contentW;
        var fy = (p.Y - offY) / contentH;
        if (fx is < 0 or > 1 || fy is < 0 or > 1) return false;

        nx = (int)(fx * 65535);
        ny = (int)(fy * 65535);
        return true;
    }

    private long _lastMoveTicks;

    private void OnScreenMouseMove(object sender, MouseEventArgs e)
    {
        if (_inputCall is null) return;
        var now = Environment.TickCount64;
        if (now - _lastMoveTicks < 16) return; // Cap mouse-move rate to ~60Hz for low latency
        _lastMoveTicks = now;
        if (TryMapToScreen(e.GetPosition(ScreenImage), out var x, out var y))
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseMove, X = x, Y = y });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private uint _lastSyncedClipSeq;

    private static ClipboardData BuildLocalClipboardData()
    {
        var data = new ClipboardData();
        if (Clipboard.ContainsFileDropList())
        {
            long total = 0;
            foreach (string? path in Clipboard.GetFileDropList())
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                var fi = new FileInfo(path);
                if (total + fi.Length > 25 * 1024 * 1024) break;
                total += fi.Length;
                data.Files.Add(new ClipboardFile
                {
                    FileName = fi.Name,
                    Data = Google.Protobuf.ByteString.CopyFrom(File.ReadAllBytes(path))
                });
            }
        }
        if (data.Files.Count == 0 && Clipboard.ContainsText())
            data.Text = Clipboard.GetText();
        return data;
    }

    private async Task SyncLocalClipboardIfChangedAsync()
    {
        if (_conn is null) return;
        var seq = GetClipboardSequenceNumber();
        if (seq == _lastSyncedClipSeq) return;
        _lastSyncedClipSeq = seq;

        var data = BuildLocalClipboardData();
        if (data.Files.Count == 0 && string.IsNullOrEmpty(data.Text)) return;

        await _conn.Client.SetClipboardAsync(data, deadline: DateTime.UtcNow.AddSeconds(15));
    }

    private async void OnScreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_inputCall is null) return;
        Keyboard.Focus(ScreenImage);
        ScreenImage.Focus();

        // Sync local files/text to remote clipboard BEFORE right-click so Windows "Paste / הדבק" appears immediately
        if (e.ChangedButton == MouseButton.Right)
        {
            try { await SyncLocalClipboardIfChangedAsync(); } catch { }
        }

        if (TryMapToScreen(e.GetPosition(ScreenImage), out var x, out var y))
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseDown, X = x, Y = y, Button = ButtonCode(e.ChangedButton) });
    }

    private void OnScreenMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_inputCall is null) return;
        if (TryMapToScreen(e.GetPosition(ScreenImage), out var x, out var y))
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseUp, X = x, Y = y, Button = ButtonCode(e.ChangedButton) });
    }

    private void OnScreenTouchDown(object sender, TouchEventArgs e)
    {
        if (_inputCall is null) return;
        e.Handled = true;
        ScreenImage.CaptureTouch(e.TouchDevice);
        Keyboard.Focus(ScreenImage);
        ScreenImage.Focus();
        var pos = e.GetTouchPoint(ScreenImage).Position;
        if (TryMapToScreen(pos, out var x, out var y))
        {
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseMove, X = x, Y = y });
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseDown, X = x, Y = y, Button = 0 });
        }
    }

    private void OnScreenTouchMove(object sender, TouchEventArgs e)
    {
        if (_inputCall is null) return;
        e.Handled = true;
        var pos = e.GetTouchPoint(ScreenImage).Position;
        if (TryMapToScreen(pos, out var x, out var y))
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseMove, X = x, Y = y });
    }

    private void OnScreenTouchUp(object sender, TouchEventArgs e)
    {
        if (_inputCall is null) return;
        e.Handled = true;
        ScreenImage.ReleaseTouchCapture(e.TouchDevice);
        var pos = e.GetTouchPoint(ScreenImage).Position;
        if (TryMapToScreen(pos, out var x, out var y))
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseUp, X = x, Y = y, Button = 0 });
    }

    private void OnScreenMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_inputCall is null) return;
        QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseWheel, Wheel = e.Delta });
    }

    private static int ResolveVirtualKey(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key
        };
        return KeyInterop.VirtualKeyFromKey(key);
    }

    private async void OnScreenKeyDown(object sender, KeyEventArgs e)
    {
        if (_inputCall is null) return;
        e.Handled = true;
        var vk = ResolveVirtualKey(e);
        if (vk == 0) return;

        // Auto-sync clipboard (files or text) on Ctrl+V before injecting V into the remote desktop
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && (e.Key == Key.V || vk == 0x56) && _conn is not null)
        {
            try { await SyncLocalClipboardIfChangedAsync(); } catch { }
        }

        QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyDown, KeyCode = vk });

        // Auto-sync clipboard on Ctrl+C after remote app copies selection
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && (e.Key == Key.C || vk == 0x43) && _conn is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(200);
                    if (_conn is null) return;
                    var remoteClip = await _conn.Client.GetClipboardAsync(new Empty(),
                        deadline: DateTime.UtcNow.AddSeconds(3));
                    if (!string.IsNullOrEmpty(remoteClip.Text))
                        Dispatcher.Invoke(() => SetLocalClipboardFromRemote(remoteClip.Text));
                }
                catch { }
            });
        }
    }

    private async void OnSendClipboardClick(object sender, RoutedEventArgs e)
    {
        if (_conn is null && !await ConnectToCurrentFieldsAsync()) return;
        try
        {
            var data = BuildLocalClipboardData();
            await _conn!.Client.SetClipboardAsync(data, deadline: DateTime.UtcNow.AddSeconds(15));
            _lastSyncedClipSeq = GetClipboardSequenceNumber();

            // If live input control is active, also synthesize Ctrl+V so it pastes immediately
            if (_inputCall is not null)
            {
                QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyDown, KeyCode = 0x11 }); // VK_CONTROL
                QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyDown, KeyCode = 0x56 }); // VK_V
                QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyUp, KeyCode = 0x56 });
                QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyUp, KeyCode = 0x11 });
            }

            StatusText.Text = data.Files.Count > 0
                ? $"Sent {data.Files.Count} file(s) to {_agentInfo?.MachineName} clipboard."
                : $"Sent clipboard to {_agentInfo?.MachineName} ({data.Text.Length} chars).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Clipboard send failed: " + Short(ex);
        }
    }

    private async void OnGetClipboardClick(object sender, RoutedEventArgs e)
    {
        if (_conn is null && !await ConnectToCurrentFieldsAsync()) return;
        try
        {
            var res = await _conn!.Client.GetClipboardAsync(new Empty(),
                deadline: DateTime.UtcNow.AddSeconds(15));
            if (res.Files.Count > 0)
            {
                var stageDir = Path.Combine(Path.GetTempPath(), "HomeRmsClipboard", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(stageDir);
                var paths = new System.Collections.Specialized.StringCollection();
                foreach (var f in res.Files)
                {
                    var safeName = Path.GetFileName(f.FileName);
                    if (string.IsNullOrWhiteSpace(safeName)) continue;
                    var dest = Path.Combine(stageDir, safeName);
                    await File.WriteAllBytesAsync(dest, f.Data.ToByteArray());
                    paths.Add(dest);
                }
                if (paths.Count > 0) Clipboard.SetFileDropList(paths);
                _lastSyncedClipSeq = GetClipboardSequenceNumber();
                StatusText.Text = $"Copied {paths.Count} file(s) from {_agentInfo?.MachineName} clipboard.";
                return;
            }
            if (!string.IsNullOrEmpty(res.Text))
                SetLocalClipboardFromRemote(res.Text);
            StatusText.Text = $"Copied clipboard from {_agentInfo?.MachineName} ({res.Text.Length} chars).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Clipboard fetch failed: " + Short(ex);
        }
    }

    private void OnScreenKeyUp(object sender, KeyEventArgs e)
    {
        if (_inputCall is null) return;
        e.Handled = true;
        var vk = ResolveVirtualKey(e);
        if (vk != 0)
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyUp, KeyCode = vk });
    }

    private static int ButtonCode(MouseButton b) => b switch
    {
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => 0
    };

    // ---------------- Silent Software Deployment ----------------
    private void OnBrowseDeployFileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Installers or Scripts (.exe, .msi, .msp, .ps1, .cmd, .bat)",
            Filter = "Packages & Scripts (*.exe;*.msi;*.msp;*.ps1;*.cmd;*.bat)|*.exe;*.msi;*.msp;*.ps1;*.cmd;*.bat|Scripts (*.ps1;*.cmd;*.bat)|*.ps1;*.cmd;*.bat|Installers (*.exe;*.msi;*.msp)|*.exe;*.msi;*.msp|All Files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true)
        {
            foreach (var file in dlg.FileNames)
                AddDeployFile(file);
            DeployFilePathBox.Text = dlg.FileNames.LastOrDefault() ?? "";
        }
    }

    private async void OnUploadAndRunScriptClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Upload & Run Silent Script (.cmd, .bat, .ps1)",
                Filter = "Scripts (*.ps1;*.cmd;*.bat)|*.ps1;*.cmd;*.bat|CMD / Batch (*.cmd;*.bat)|*.cmd;*.bat|PowerShell (*.ps1)|*.ps1|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            DeployFilePathBox.Text = dlg.FileName;
            if (AddDeployFile(dlg.FileName) is { IsBusy: false } item)
                await DeployLocalFileCoreAsync(item, DeployArgsBox.Text.Trim());
        }
        catch (Exception ex)
        {
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}");
        }
    }

    private void OnDeployDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDeployFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;
        var added = 0;
        foreach (var file in files)
        {
            if (System.IO.Directory.Exists(file))
            {
                AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] [file] Skipped folder '{file}': drop the installer files themselves.");
                continue;
            }
            if (AddDeployFile(file) is not null) added++;
        }
        if (added > 0)
        {
            DeployShell.Show(Views.Deploy.DeployView.Files);
            DeployFilePathBox.Text = files[^1];
            StatusText.Text = $"Added {added} file(s) to the deploy list. Click Run Files to upload and run them on the connected agent.";
        }
    }

    private void OnDeployPresetFlagClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string flags })
            DeployArgsBox.Text = flags;
    }

    private void OnClearDeployLogClick(object sender, RoutedEventArgs e)
    {
        DeployOutputBox.Clear();
        FilesV.LogBox.Clear();
    }

    private async void OnStartDeployClick(object sender, RoutedEventArgs e)
    {
        var path = DeployFilePathBox.Text.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            AddDeployFile(path);

        if (!_deployFiles.Any(f => f.DeployStatus == DeployItemStatus.Pending))
        {
            MessageBox.Show("Drop or browse to one or more local installers or scripts (.exe, .msi, .msp, .ps1, .cmd, or .bat) to upload.",
                "Home RMS — Software Deploy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunPendingDeployFilesAsync();
    }

    /// <summary>Streams one file to the connected agent via DeployPackage and reports upload %, exit code and output.</summary>
    private async Task<bool> DeployLocalFileCoreAsync(DeployFileItem item, string args)
    {
        if (_conn is null && !await ConnectToCurrentFieldsAsync())
        {
            item.Detail = "Not connected to an agent.";
            return false;
        }

        var filePath = item.FullPath;
        var fileName = item.FileName;
        var target = _agentInfo?.MachineName ?? _conn!.Host;
        var prefix = $"[{target}] [{fileName}]";

        try
        {
            var fileInfo = new FileInfo(filePath);
            var totalBytes = fileInfo.Length;

            DeployRunBtn.IsEnabled = false;
            item.Percent = 0;
            item.DeployStatus = DeployItemStatus.Downloading;
            item.Detail = "Uploading...";
            DeployStatusBadge.Text = "Uploading…";
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] {prefix} Uploading {totalBytes / 1024.0 / 1024.0:F2} MB…");

            using var call = _conn!.Client.DeployPackage(deadline: DateTime.UtcNow.AddMinutes(10));
            var buffer = new byte[256 * 1024]; // 256 KB chunks
            long sent = 0;
            var first = true;
            var lastLoggedPct = -1;

            await using (var fs = File.OpenRead(filePath))
            {
                int read;
                while ((read = await fs.ReadAsync(buffer)) > 0)
                {
                    var chunk = new PackageChunk
                    {
                        Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read)
                    };
                    if (first)
                    {
                        chunk.FileName = fileName;
                        chunk.Arguments = string.IsNullOrWhiteSpace(args) ? "AUTO" : args;
                        chunk.TimeoutSeconds = 300;
                        first = false;
                    }

                    await call.RequestStream.WriteAsync(chunk);
                    sent += read;
                    var pct = totalBytes > 0 ? (int)(sent * 100 / totalBytes) : 100;
                    item.Percent = pct;
                    DeployStatusBadge.Text = $"Uploading {pct}%";
                    if (pct / 25 != lastLoggedPct / 25 && pct < 100)
                    {
                        lastLoggedPct = pct;
                        AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] {prefix} Upload {pct}%");
                    }
                }
            }

            var ext = fileInfo.Extension;
            var isScript = ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);

            await call.RequestStream.CompleteAsync();
            item.Percent = -1;
            item.DeployStatus = DeployItemStatus.Installing;
            item.Detail = isScript ? "Running script..." : "Running installer...";
            DeployStatusBadge.Text = isScript ? "Running Script Silently…" : "Installing Silently…";
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] {prefix} Upload complete (100%). {(isScript ? "Running silent script" : "Running silent installer")}…");

            var result = await call.ResponseAsync;
            DeployStatusBadge.Text = result.Success
                ? (isScript ? $"Script Done (Exit {result.ExitCode})" : $"Installed (Exit {result.ExitCode})")
                : $"Failed (Exit {result.ExitCode})";
            item.DeployStatus = result.Success ? DeployItemStatus.Success : DeployItemStatus.Failed;
            item.Detail = $"Exit code {result.ExitCode} (0x{unchecked((uint)result.ExitCode):X8})";
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] {prefix} Command: {result.CommandLine}");
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] {prefix} Result : {(result.Success ? "SUCCESS" : "FAILED")} (exit code {result.ExitCode})");
            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                foreach (var line in result.Output.Split('\n'))
                    if (line.Trim().Length > 0)
                        AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] {prefix} {line.TrimEnd('\r')}");
            }

            if (result.Success)
                await RefreshInventoryCoreAsync();
            return result.Success;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated)
        {
            const string msg = "Endpoint refused the upload: pair this endpoint first (connect from Fleet and enter the agent's 2FA code).";
            item.DeployStatus = DeployItemStatus.Failed;
            item.Detail = msg;
            DeployStatusBadge.Text = "Deploy Failed";
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] {prefix} ERROR: {msg}");
            return false;
        }
        catch (Exception ex)
        {
            item.DeployStatus = DeployItemStatus.Failed;
            item.Detail = ex is RpcException rpc ? $"{rpc.StatusCode}: {rpc.Status.Detail}" : ex.Message;
            DeployStatusBadge.Text = "Deploy Failed";
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] {prefix} ERROR: {item.Detail}");
            return false;
        }
        finally
        {
            DeployRunBtn.IsEnabled = true;
        }
    }

    private void AppendDeployLog(string line)
    {
        // The same live log shows in the Install view's Deploy tab and on the Files tab.
        foreach (var box in new[] { DeployOutputBox, FilesV.LogBox })
        {
            // Keep the live log bounded (RunWinget can stream a lot of lines).
            if (box.Text.Length > 400_000)
            {
                var text = box.Text;
                var cut = text.IndexOf('\n', text.Length - 300_000);
                box.Text = "… (older log lines trimmed)\r\n" + (cut >= 0 ? text[(cut + 1)..] : text[^300_000..]);
            }
            box.AppendText(line + "\r\n");
            box.ScrollToEnd();
        }
        MirrorDeployLineToConsole(line); // MainWindow.Shell.cs
    }

    // ---------------- Automated Screenshot Generation ----------------
    private async Task RunAutomatedScreenshotsAsync(string outDir, int port)
    {
        Directory.CreateDirectory(outDir);
        PortBox.Text = port.ToString();
        HostBox.Text = "localhost";
        if (_machines.Count > 0)
        {
            _machines[0].Port = port;
            _machines[0].Name = $"{Environment.MachineName} (Primary Workstation)";
        }
        if (_machines.Count < 2)
        {
            _machines.Add(new MachineProfile
            {
                Name = "Living-Room-MediaPC",
                Host = "192.168.1.42",
                Port = 7443,
                IsOnline = true,
                StatusLabel = "Online",
                UserAndOs = "Family • 3840×2160"
            });
            _machines.Add(new MachineProfile
            {
                Name = "Home-Lab-NAS",
                Host = "192.168.1.15",
                Port = 7443,
                IsOnline = false,
                StatusLabel = "Offline",
                UserAndOs = "Server • Headless"
            });
        }
        // Connect-by-computer-name samples (never saved: MachineStore is read-only in screenshot runs).
        _machines.Insert(Math.Min(1, _machines.Count), new MachineProfile
        {
            Name = "Kitchen-Laptop",
            Host = "kitchen-laptop",
            Port = 7443,
            ComputerName = "KITCHEN-LAPTOP",
            LastKnownIp = "192.168.1.140",
            LastSeenUtc = DateTime.UtcNow.AddHours(-20),
            StatusLabel = "Offline",
        });
        _machines.Insert(Math.Min(2, _machines.Count), new MachineProfile
        {
            Name = "Study-PC",
            Host = "192.168.1.57",
            Port = 7443,
            ComputerName = "STUDY-PC",
            LastKnownIp = "192.168.1.57",
            IsOnline = true,
            StatusLabel = "Online",
            UserAndOs = "Kids • 1920×1080",
            LastSeenUtc = DateTime.UtcNow,
        });

        try { await ConnectToCurrentFieldsAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        AgentPair2FaOverlay.Visibility = Visibility.Collapsed;
        LockOverlay.Visibility = Visibility.Collapsed;
        await Task.Delay(400);
        NavOverview.IsChecked = true;
        await Task.Delay(350);
        SaveWindowPng(Path.Combine(outDir, "controller-overview.png"));

        NavFleet.IsChecked = true;
        await Task.Delay(350);
        SaveWindowPng(Path.Combine(outDir, "controller-fleet.png"));

        NavInventory.IsChecked = true;
        await Task.Delay(350);
        SaveWindowPng(Path.Combine(outDir, "controller-inventory.png"));

        // Switch to Shell tab, run a real PowerShell command, capture screenshot
        NavShell.IsChecked = true;
        ShellInputBox.Text = "Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 8 Id, ProcessName, @{N='RAM_MB';E={[math]::Round($_.WorkingSet64/1MB,1)}} | Format-Table -AutoSize";
        try { await SendShellLineAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        await Task.Delay(1200);
        SaveWindowPng(Path.Combine(outDir, "controller-shell.png"));

        // Switch to Remote Desktop tab, capture live frame, capture screenshot
        NavDesktop.IsChecked = true;
        ControlCheck.IsChecked = true;
        _ = StartStreamCoreAsync();
        await Task.Delay(1200);
        SaveWindowPng(Path.Combine(outDir, "controller-desktop.png"));
        StopStream();

        // Switch to Deploy (winstall-style catalog) and capture its sub-views
        NavDeploy.IsChecked = true;
        await Task.Delay(450);
        SaveWindowPng(Path.Combine(outDir, "controller-deploy.png"));
        await CaptureDeployScreenshotsAsync(outDir);

        // Switch to Settings (Windows 11 Settings UI)
        NavSystem.IsChecked = true;
        await Task.Delay(400);
        SaveWindowPng(Path.Combine(outDir, "controller-settings.png"));

        // Remaining views, drawers and overlays (redesign coverage; nothing here is persisted)
        foreach (var (nav, name) in new[] { (NavUpdates, "updates"), (NavFiles, "files"), (NavServices, "services"), (NavScripts, "scripts") })
        {
            nav.IsChecked = true;
            await Task.Delay(450);
            SaveWindowPng(Path.Combine(outDir, $"controller-{name}.png"));
        }

        NavOverview.IsChecked = true;
        AddActivity("FLEET", "Screenshot run: sample activity line for the live console.");
        SetConsoleOpen(true, persist: false);
        await Task.Delay(450);
        SaveWindowPng(Path.Combine(outDir, "controller-console.png"));
        SetConsoleOpen(false, persist: false);

        _sidebarCollapsed = true;
        ApplySidebarState(animate: false);
        NavFleet.IsChecked = true;
        await Task.Delay(450);
        SaveWindowPng(Path.Combine(outDir, "controller-sidebar-collapsed.png"));
        _sidebarCollapsed = false;
        ApplySidebarState(animate: false);

        EndpointDrawerOverlay.Visibility = Visibility.Visible;
        await Task.Delay(350);
        SaveWindowPng(Path.Combine(outDir, "controller-endpoint-drawer.png"));
        EndpointDrawerOverlay.Visibility = Visibility.Collapsed;

        // Edit an endpoint whose computer name is known: "Connect using" choice, then an invalid host.
        if (_machines.FirstOrDefault(m => m.ComputerName == "STUDY-PC") is { } editSample)
        {
            OnCardEditClick(new Button { Tag = editSample }, new RoutedEventArgs());
            await Task.Delay(350);
            SaveWindowPng(Path.Combine(outDir, "controller-endpoint-edit.png"));
            HostBox.Text = "study pc";
            ValidateDrawerAddress(BuildProfileFromDrawer());
            await Task.Delay(250);
            SaveWindowPng(Path.Combine(outDir, "controller-endpoint-edit-invalid.png"));
            EndpointDrawerOverlay.Visibility = Visibility.Collapsed;
        }

        AlertDrawerOverlay.Visibility = Visibility.Visible;
        await Task.Delay(350);
        SaveWindowPng(Path.Combine(outDir, "controller-alerts.png"));
        AlertDrawerOverlay.Visibility = Visibility.Collapsed;

        try { OnOpenAboutClick(this, new RoutedEventArgs()); } catch { AboutOverlay.Visibility = Visibility.Visible; }
        await Task.Delay(450);
        // Never write real 2FA material into a screenshot
        AboutTotpSecretBox.Text = "•••• redacted ••••";
        AboutBackupCodesBox.Text = "•••• redacted ••••";
        AboutFingerprintBox.Text = "•••• redacted ••••";
        AboutHubKeyBox.Text = "•••• redacted ••••";
        AboutQrImage.Source = null;
        SecurityDiagText.Text = "(redacted for screenshot)";
        SaveWindowPng(Path.Combine(outDir, "controller-about.png"));
        AboutOverlay.Visibility = Visibility.Collapsed;

        LockOverlay.Visibility = Visibility.Visible;
        await Task.Delay(350);
        SaveWindowPng(Path.Combine(outDir, "controller-lock.png"));


        Application.Current.Shutdown(0);
        Environment.Exit(0);
    }

    private void SaveWindowPng(string filePath)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)(ActualWidth * dpi.DpiScaleX);
        var h = (int)(ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;

        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    // ---------------- Helpers ----------------
    private static long Gb(long bytes) => bytes / (1024 * 1024 * 1024);
    private static string Short(Exception ex) =>
        ex is RpcException or AgentPairingException ? AgentErrors.Describe(ex) : ex.Message.Split('\n')[0];

    protected override void OnClosed(EventArgs e)
    {
        Disconnect();
        try { _centralHub.Dispose(); } catch { }
        base.OnClosed(e);
    }
}
