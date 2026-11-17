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
using HomeRms.Shared;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

namespace HomeRms.Controller;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MachineProfile> _machines = new();
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

        // Ensure certs exist so probing/connecting works out of the box in dev/home setups
        try { DevCertificates.EnsureDeploymentCertificates(RmsPaths.CertDir, RmsPaths.CertPassword); }
        catch { /* surfaced on connect if missing */ }

        foreach (var m in MachineStore.Load())
            _machines.Add(m);

        MachinesList.ItemsSource = _machines;
        MachineSwitcher.ItemsSource = _machines;
        if (_machines.Count > 0)
        {
            MachinesList.SelectedIndex = 0;
            MachineSwitcher.SelectedIndex = 0;
        }

        ShellOutput.Text =
            "Home RMS Interactive PowerShell Console\r\n" +
            "Connected over Mutual TLS (HTTP/2). Every command executed here is recorded in the remote agent's visible activity log.\r\n" +
            "--------------------------------------------------------------------------------------------------------------------\r\n";

        DeployOutputBox.Text =
            "Home RMS Silent Software Deployment Console\r\n" +
            "Select or drag & drop any .exe, .msi, .msp, or .ps1 file above to stream it over mTLS and install it silently on the remote computer.\r\n" +
            "--------------------------------------------------------------------------------------------------------------------\r\n";

        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        var shotDir = GetArg(args, "--screenshot-dir");
        var portArg = GetArg(args, "--port");
        if (!string.IsNullOrWhiteSpace(shotDir))
        {
            var port = int.TryParse(portArg, out var p) ? p : RmsPaths.DefaultPort;
            await RunAutomatedScreenshotsAsync(shotDir, port);
            return;
        }

        await ProbeAllMachinesAsync();
        _ = CheckGitHubUpdateInBackgroundAsync();
    }

    private AvailableUpdate? _pendingUpdate;

    private async Task CheckGitHubUpdateInBackgroundAsync()
    {
        try
        {
            await Task.Delay(2500);
            var update = await ControllerUpdater.CheckForUpdateAsync();
            if (update is not null)
            {
                _pendingUpdate = update;
                UpdateBtn.Content = $"⬇ Update {update.Tag}";
                UpdateBtn.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                UpdateBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                UpdateBtn.Foreground = Brushes.White;
                UpdateBtn.ToolTip = $"New version {update.Tag} available on GitHub. Click to update & restart.";
            }
            else
            {
                UpdateBtn.Content = $"v{ControllerUpdater.CurrentVersion.Major}.{ControllerUpdater.CurrentVersion.Minor}.{ControllerUpdater.CurrentVersion.Build} ✓";
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
                UpdateBtn.Content = "Checking GitHub…";
                _pendingUpdate = await ControllerUpdater.CheckForUpdateAsync();
                if (_pendingUpdate is null)
                {
                    UpdateBtn.Content = $"v{ControllerUpdater.CurrentVersion.Major}.{ControllerUpdater.CurrentVersion.Minor}.{ControllerUpdater.CurrentVersion.Build} ✓";
                    MessageBox.Show($"You are running the latest version (v{ControllerUpdater.CurrentVersion}).",
                        "Home RMS — Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                UpdateBtn.Content = $"⬇ Update {_pendingUpdate.Tag}";
                UpdateBtn.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                UpdateBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                UpdateBtn.Foreground = Brushes.White;
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

    // ---------------- Navigation ----------------
    private void OnNavTabChanged(object sender, RoutedEventArgs e)
    {
        if (FleetPanel is null || InventoryPanel is null || DesktopPanel is null || ShellPanel is null || DeployPanel is null || SystemPanel is null) return;

        if (_isFullScreen && NavDesktop.IsChecked != true)
            SetFullScreen(false);

        FleetPanel.Visibility = NavFleet.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        InventoryPanel.Visibility = NavInventory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DesktopPanel.Visibility = NavDesktop.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShellPanel.Visibility = NavShell.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DeployPanel.Visibility = NavDeploy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        var showSettings = NavUpdates.IsChecked == true || NavSystem.IsChecked == true;
        SystemPanel.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;

        if (showSettings)
        {
            if (_win11Page is null) InitWin11Settings();
            if (NavUpdates.IsChecked == true)
                SelectWin11Page(SystemCatalog.Win11Pages.First(p => p.Id == "updates"));
            else if (NavSystem.IsChecked == true && _win11Page?.Id == "updates")
                SelectWin11Page(SystemCatalog.Win11Pages[0]);
        }
    }

    // ---------------- Windows 11 Style Settings & Updates ----------------
    private SystemCatalog.Win11SettingsPage? _win11Page;
    private string _win11LastArea = "system";
    private SystemCatalog.Table _sysTable = SystemCatalog.Table.Empty;
    private List<Dictionary<string, string>> _sysVisible = new();

    // Segoe Fluent Icons glyphs for the settings categories and setting rows
    private static string PageGlyph(string pageId) => pageId switch
    {
        "home" => "",
        "system" => "",
        "devices" => "",
        "network" => "",
        "personalization" => "",
        "apps" => "",
        "accounts" => "",
        "time" => "",
        "gaming" => "",
        "privacy" => "",
        "updates" => "",
        _ => ""
    };

    private static string AreaGlyph(string area) => area switch
    {
        "updates" => "",
        "services" => "",
        "power" => "",
        "startup" => "",
        "tasks" => "",
        "registry" => "",
        "firewall" => "",
        "system" => "",
        _ => ""
    };

    private void InitWin11Settings()
    {
        Win11SidebarPanel.Children.Clear();
        foreach (var page in SystemCatalog.Win11Pages)
        {
            var item = new RadioButton
            {
                Content = page.Title,
                Tag = PageGlyph(page.Id),
                DataContext = page,
                GroupName = "Win11Settings",
                Style = (Style)FindResource("NavItem")
            };
            item.Checked += (_, _) => { if (!ReferenceEquals(_win11Page, page)) SelectWin11Page(page); };
            Win11SidebarPanel.Children.Add(item);
        }
        SelectWin11Page(SystemCatalog.Win11Pages[0]);
    }

    private void SelectWin11Page(SystemCatalog.Win11SettingsPage page)
    {
        _win11Page = page;
        foreach (RadioButton item in Win11SidebarPanel.Children)
            item.IsChecked = ReferenceEquals(item.DataContext, page);

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
        Win11PageTitle.Text = $"Results for \"{q}\"";
        Win11PageSubtitle.Text = matches.Count == 1 ? "1 matching setting" : $"{matches.Count} matching settings";
        RenderWin11Cards(matches);
    }

    private void RenderWin11Cards(IReadOnlyList<SystemCatalog.Win11SettingItem> items)
    {
        Win11CardsPanel.Children.Clear();
        foreach (var item in items)
        {
            var card = new Border { Style = (Style)FindResource("SettingCard") };

            var dock = new DockPanel();
            var btn = new Button
            {
                Content = item.ButtonLabel,
                Style = (Style)FindResource(item.Dangerous ? "DangerSubtleButton" : "StandardButton"),
                MinWidth = 110,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (item.Dangerous) btn.ToolTip = "Changes the remote PC — you will be asked to confirm";
            DockPanel.SetDock(btn, Dock.Right);
            btn.Click += async (_, _) => await RunWin11ItemAsync(item);

            var glyph = new TextBlock
            {
                Text = AreaGlyph(item.Area),
                Style = (Style)FindResource("IconGlyph"),
                FontSize = 20,
                Width = 20,
                Margin = new Thickness(4, 0, 18, 0)
            };
            DockPanel.SetDock(glyph, Dock.Left);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            textStack.Children.Add(new TextBlock { Text = item.Title, FontSize = 14 });
            var sub = new TextBlock { Text = item.Subtitle, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("SecondaryText") };
            textStack.Children.Add(sub);

            dock.Children.Add(btn);
            dock.Children.Add(glyph);
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
            SystemMessageText.Text = $"'{item.Title}' needs a Target ({_win11Page?.TargetHint}).";
            SystemMessageText.Foreground = ConsoleWarnBrush;
            return;
        }

        if (item.Dangerous && MessageBox.Show(
                $"{item.Title}\nTarget: {(target.Length == 0 ? "(none)" : target)}\n\nApply this on {_agentInfo?.MachineName ?? "the remote PC"}?",
                "Home RMS — Confirm Setting Change", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _win11LastArea = item.Area;
        try
        {
            SystemMessageText.Text = $"Running {item.Area}/{item.Action}…";
            SystemMessageText.Foreground = ConsoleMutedBrush;
            var res = await _conn!.Client.SystemOpAsync(
                new SystemOpRequest { Area = item.Area, Action = item.Action, Target = target, Extra = extra },
                deadline: DateTime.UtcNow.AddMinutes(item.LongRunning ? 50 : 2.2));

            SystemAdminText.Text = res.IsElevated ? "Agent: administrator" : "Agent: standard user (run with --elevate)";
            SetPill(SystemAdminBadge, SystemAdminText, res.IsElevated ? Tone.Success : Tone.Caution);

            SetSystemTable(SystemCatalog.ParseTable(res.JsonData));
            SystemMessageText.Text = (res.Success ? "✔ " : "✖ ") + res.Message;
            SystemMessageText.Foreground = res.Success ? ConsoleOkBrush : ConsoleErrorBrush;
        }
        catch (Exception ex)
        {
            SystemMessageText.Text = "✖ " + Short(ex);
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

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
            Keyboard.FocusedElement is not TextBox)
        {
            OnScreenKeyDown(sender, e);
        }
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (NavDesktop.IsChecked == true && _inputCall is not null &&
            Keyboard.FocusedElement is not TextBox)
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

            RailColumn.Width = new GridLength(76);
            RailBorder.Visibility = Visibility.Visible;
            TopBarBorder.Visibility = Visibility.Visible;
            DesktopToolbarBorder.Visibility = Visibility.Visible;
            WorkspaceGrid.Margin = new Thickness(20, 16, 20, 18);
            ScreenCanvasBorder.CornerRadius = new CornerRadius(14);
            ScreenCanvasBorder.BorderThickness = new Thickness(1);
            FullscreenHud.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- Machine List & Probing ----------------
    private void OnMachineSwitcherChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MachineSwitcher.SelectedItem is not MachineProfile m) return;
        if (!ReferenceEquals(MachinesList.SelectedItem, m))
            MachinesList.SelectedItem = m;
    }

    private void OnMachineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MachinesList.SelectedItem is not MachineProfile m) return;
        if (!ReferenceEquals(MachineSwitcher.SelectedItem, m))
            MachineSwitcher.SelectedItem = m;
        NameBox.Text = m.Name;
        HostBox.Text = m.Host;
        PortBox.Text = m.Port.ToString();

        if (_conn is null)
        {
            TargetTitleText.Text = m.DisplayTitle;
            StatusText.Text = $"{m.Address}  ·  Click Connect to open a mutual-TLS session.";
        }
    }

    private async void OnMachineDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MachinesList.SelectedItem is not MachineProfile) return;
        if (_conn is not null) Disconnect();
        await ConnectToCurrentFieldsAsync();
    }

    private void OnSaveMachineClick(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host)) return;
        if (!int.TryParse(PortBox.Text, out var port)) port = 7443;
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? host : NameBox.Text.Trim();

        var existing = _machines.FirstOrDefault(m =>
            string.Equals(m.Host, host, StringComparison.OrdinalIgnoreCase) && m.Port == port);

        if (existing is not null)
        {
            existing.Name = name;
            MachinesList.SelectedItem = existing;
        }
        else
        {
            var created = new MachineProfile { Name = name, Host = host, Port = port, StatusLabel = "Saved" };
            _machines.Add(created);
            MachinesList.SelectedItem = created;
        }

        MachineStore.Save(_machines);
        _ = ProbeAllMachinesAsync();
    }

    private void OnRemoveMachineClick(object sender, RoutedEventArgs e)
    {
        if (MachinesList.SelectedItem is not MachineProfile selected) return;
        if (_connectedProfile == selected) Disconnect();
        _machines.Remove(selected);
        MachineStore.Save(_machines);
        if (_machines.Count > 0) MachinesList.SelectedIndex = 0;
    }

    private async void OnProbeMachinesClick(object sender, RoutedEventArgs e) => await ProbeAllMachinesAsync();

    private async Task ProbeAllMachinesAsync()
    {
        if (ProbeBtn is not null) ProbeBtn.IsEnabled = false;
        try
        {
            await DiscoverLanAgentsAsync();

            var tasks = _machines.ToList().Select(async m =>
            {
                if (m == _connectedProfile && _conn is not null)
                {
                    m.IsOnline = true;
                    m.IsConnected = true;
                    m.StatusLabel = "Connected";
                    return;
                }

                try
                {
                    using var temp = new RmsConnection(m.Host, m.Port);
                    var info = await temp.Client.GetAgentInfoAsync(
                        new Empty(), deadline: DateTime.UtcNow.AddSeconds(2.5));
                    m.IsOnline = true;
                    m.IsConnected = false;
                    m.StatusLabel = "Online";
                    m.UserAndOs = $"{info.LoggedInUser} • {info.PrimaryScreenWidth}×{info.PrimaryScreenHeight}";
                }
                catch
                {
                    m.IsOnline = false;
                    m.IsConnected = false;
                    m.StatusLabel = "Offline";
                }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            if (ProbeBtn is not null) ProbeBtn.IsEnabled = true;
        }
    }

    private async Task DiscoverLanAgentsAsync()
    {
        try
        {
            using var udp = new UdpClient { EnableBroadcast = true };
            var probe = Encoding.UTF8.GetBytes("HOMERMS_DISCOVER_V1");
            await udp.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, 7443));

            var changed = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(450));
            while (!cts.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.UTF8.GetString(packet.Buffer).Trim();
                var parts = text.Split('|');
                if (parts.Length < 3 || parts[0] != "HOMERMS_AGENT_V1") continue;

                var machineName = parts[1];
                if (!int.TryParse(parts[2], out var port)) port = 7443;
                var ip = packet.RemoteEndPoint.Address.ToString();

                // Skip our own machine if localhost is already listed
                if (string.Equals(machineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
                    _machines.Any(m => m.Host is "localhost" or "127.0.0.1"))
                {
                    continue;
                }

                var existing = _machines.FirstOrDefault(m =>
                    (string.Equals(m.Host, ip, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(m.Name, machineName, StringComparison.OrdinalIgnoreCase)) &&
                    m.Port == port);

                if (existing is null)
                {
                    _machines.Add(new MachineProfile
                    {
                        Name = machineName,
                        Host = ip,
                        Port = port,
                        StatusLabel = "Discovered"
                    });
                    changed = true;
                }
                else if (existing.Host != ip && !string.Equals(existing.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    existing.Host = ip;
                    changed = true;
                }
            }

            if (changed)
                MachineStore.Save(_machines);
        }
        catch
        {
            // Expected timeout when UDP listen window closes
        }
    }

    // ---------------- Connection ----------------
    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_conn is not null)
        {
            Disconnect();
            return;
        }
        await ConnectToCurrentFieldsAsync();
    }

    private async Task<bool> ConnectToCurrentFieldsAsync()
    {
        var host = HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host)) host = "localhost";
        if (!int.TryParse(PortBox.Text, out var port)) port = 7443;

        try
        {
            ConnectBtn.IsEnabled = false;
            StatusText.Text = $"Establishing Mutual-TLS handshake with {host}:{port}…";
            SetBadge("Connecting…", Tone.Caution);

            var sw = Stopwatch.StartNew();
            var conn = new RmsConnection(host, port);
            _agentInfo = await conn.Client.GetAgentInfoAsync(new Empty(),
                deadline: DateTime.UtcNow.AddSeconds(8));
            sw.Stop();
            _conn = conn;
            _frameWidth = _agentInfo.PrimaryScreenWidth;
            _frameHeight = _agentInfo.PrimaryScreenHeight;

            var profile = _machines.FirstOrDefault(m =>
                string.Equals(m.Host, host, StringComparison.OrdinalIgnoreCase) && m.Port == port);
            if (profile is not null)
            {
                _connectedProfile = profile;
                profile.IsOnline = true;
                profile.IsConnected = true;
                profile.StatusLabel = "Connected";
                profile.UserAndOs = $"{_agentInfo.LoggedInUser} • {_agentInfo.PrimaryScreenWidth}×{_agentInfo.PrimaryScreenHeight}";
            }

            TargetTitleText.Text = _agentInfo.MachineName;
            StatusText.Text =
                $"{host}:{port}  ·  {_agentInfo.LoggedInUser}  ·  {_agentInfo.PrimaryScreenWidth}×{_agentInfo.PrimaryScreenHeight}  ·  {_agentInfo.OsVersion}";
            SetBadge($"Connected · {sw.ElapsedMilliseconds} ms", Tone.Success);

            ConnectBtn.Content = "Disconnect";
            ConnectBtn.Style = (Style)FindResource("DangerButton");

            await RefreshInventoryCoreAsync();
            return true;
        }
        catch (Exception ex)
        {
            _conn = null;
            SetBadge("Connection failed", Tone.Critical);
            StatusText.Text = "Could not connect: " + Short(ex);
            MessageBox.Show($"Could not connect to {host}:{port}\n\n{ex.Message}",
                "Home RMS — Connection Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
        }
    }

    private void Disconnect()
    {
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

        ConnectBtn.Content = "Connect";
        ConnectBtn.Style = (Style)FindResource("PrimaryButton");
        SetBadge("Disconnected", Tone.Neutral);
        StatusText.Text = "Session closed. Select Connect to start a new session.";
    }

    private enum Tone { Neutral, Success, Caution, Critical }

    // Fixed colours for status text drawn on the always-dark console surfaces
    private static readonly Brush ConsoleMutedBrush = Frozen(0xB3, 0xFF, 0xFF, 0xFF);
    private static readonly Brush ConsoleOkBrush = Frozen(0xFF, 0x6C, 0xCB, 0x5F);
    private static readonly Brush ConsoleWarnBrush = Frozen(0xFF, 0xF9, 0xF1, 0xA5);
    private static readonly Brush ConsoleErrorBrush = Frozen(0xFF, 0xFF, 0x99, 0xA4);

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Colours a status pill with Fluent theme tokens so it follows light/dark mode.</summary>
    private static void SetPill(Border pill, TextBlock text, Tone tone)
    {
        var (bg, fg) = tone switch
        {
            Tone.Success => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            Tone.Caution => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            Tone.Critical => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            _ => ("SystemFillColorSolidNeutralBackgroundBrush", "TextFillColorSecondaryBrush")
        };
        pill.SetResourceReference(Border.BackgroundProperty, bg);
        text.SetResourceReference(TextBlock.ForegroundProperty, fg);
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
        var q = SearchBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(q))
        {
            AppsGrid.ItemsSource = _allApps;
            ProcGrid.ItemsSource = _allProcs;
            AppsCountText.Text = $"{_allApps.Count} apps";
            ProcsCountText.Text = $"{_allProcs.Count} processes";
            return;
        }

        var filteredApps = _allApps.Where(a =>
            a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        var filteredProcs = _allProcs.Where(p =>
            p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            p.Pid.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        AppsGrid.ItemsSource = filteredApps;
        ProcGrid.ItemsSource = filteredProcs;
        AppsCountText.Text = $"{filteredApps.Count} / {_allApps.Count} apps";
        ProcsCountText.Text = $"{filteredProcs.Count} / {_allProcs.Count} procs";
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
        if (_conn is null)
        {
            if (!await ConnectToCurrentFieldsAsync()) return;
        }

        var line = ShellInputBox.Text.Trim();
        if (string.IsNullOrEmpty(line)) return;

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

        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;
        StreamBtn.Content = "Stop viewing";
        StreamBtn.Style = (Style)FindResource("DangerButton");
        StreamStatus.Text = "Awaiting on-machine consent…";

        var fps = int.TryParse(FpsBox.Text, out var f) ? Math.Clamp(f, 1, 60) : 60;
        var quality = int.TryParse(QualityBox.Text, out var q) ? Math.Clamp(q, 20, 95) : 80;
        var maxWidth = ScaleBox.SelectedItem is ComboBoxItem { Tag: string ts } && int.TryParse(ts, out var mw) ? mw : 0;

        var stats = new StreamStats();
        var statsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statsTimer.Tick += (_, _) => UpdateStreamStatus(stats);
        statsTimer.Start();

        _controlWanted = ControlCheck.IsChecked == true;
        var client = _conn.Client;

        try
        {
            // Read + decode on a worker thread; only painting happens on the UI thread
            await Task.Run(async () =>
            {
            using var call = client.StreamScreen(
                new ScreenRequest
                {
                    Fps = fps,
                    Quality = quality,
                    OperatorName = Environment.UserName,
                    MaxWidth = maxWidth,
                    SupportsTiles = true
                },
                cancellationToken: token);

            await foreach (var frame in call.ResponseStream.ReadAllAsync(token))
            {
                if (_inputCall is null && Volatile.Read(ref _controlWanted))
                    Dispatcher.Invoke(StartInputStream);

                // Decode off the UI thread (JPEG tiles in parallel)
                var update = ScreenUpdate.Decode(frame);
                stats.Add(frame.CalculateSize(), update.Regions.Count > 0, frame.Width, frame.Height);

                // Tiles must be applied in order, so instead of dropping frames we slow the
                // network read down when the UI falls behind (HTTP/2 back-pressure reaches the agent).
                while (Volatile.Read(ref _uiQueued) > 2 && !token.IsCancellationRequested)
                    await Task.Delay(1, token);

                Interlocked.Increment(ref _uiQueued);
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
                {
                    try { ApplyScreenUpdate(update); }
                    finally { Interlocked.Decrement(ref _uiQueued); }
                });
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
            Dispatcher.Invoke(() => { StreamStatus.Text = "Stream ended: " + Short(ex); });
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                statsTimer.Stop();
                StopStream();
            });
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

    private void OnScreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_inputCall is null) return;
        Keyboard.Focus(ScreenImage);
        ScreenImage.Focus();
        if (TryMapToScreen(e.GetPosition(ScreenImage), out var x, out var y))
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseDown, X = x, Y = y, Button = ButtonCode(e.ChangedButton) });
    }

    private void OnScreenMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_inputCall is null) return;
        if (TryMapToScreen(e.GetPosition(ScreenImage), out var x, out var y))
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseUp, X = x, Y = y, Button = ButtonCode(e.ChangedButton) });
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

        // Auto-sync clipboard on Ctrl+V before injecting V into the remote desktop
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && (e.Key == Key.V || vk == 0x56) && _conn is not null)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var localText = Clipboard.GetText();
                    await _conn.Client.SetClipboardAsync(new ClipboardData { Text = localText },
                        deadline: DateTime.UtcNow.AddSeconds(3));
                }
            }
            catch { }
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
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            await _conn!.Client.SetClipboardAsync(new ClipboardData { Text = text },
                deadline: DateTime.UtcNow.AddSeconds(5));

            // If live input control is active, also synthesize Ctrl+V so it pastes immediately
            if (_inputCall is not null)
            {
                QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyDown, KeyCode = 0x11 }); // VK_CONTROL
                QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyDown, KeyCode = 0x56 }); // VK_V
                QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyUp, KeyCode = 0x56 });
                QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyUp, KeyCode = 0x11 });
            }

            StatusText.Text = $"Sent clipboard to {_agentInfo?.MachineName} ({text.Length} chars).";
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
                deadline: DateTime.UtcNow.AddSeconds(5));
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
            Title = "Select Installer Package (.exe, .msi, .msp, .ps1)",
            Filter = "Installer Packages (*.exe;*.msi;*.msp;*.ps1)|*.exe;*.msi;*.msp;*.ps1|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            DeployFilePathBox.Text = dlg.FileName;
        }
    }

    private void OnDeployDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDeployFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            DeployFilePathBox.Text = files[0];
        }
    }

    private void OnDeployPresetFlagClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string flags })
            DeployArgsBox.Text = flags;
    }

    private void OnClearDeployLogClick(object sender, RoutedEventArgs e) => DeployOutputBox.Clear();

    private async void OnStartDeployClick(object sender, RoutedEventArgs e)
    {
        var path = DeployFilePathBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show("Please select a valid local installer file (.exe, .msi, .msp, or .ps1) to upload.",
                "Home RMS — Software Deploy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await DeployLocalFileCoreAsync(path, DeployArgsBox.Text.Trim());
    }

    private async Task DeployLocalFileCoreAsync(string filePath, string args)
    {
        if (_conn is null && !await ConnectToCurrentFieldsAsync())
            return;

        var fileInfo = new FileInfo(filePath);
        var fileName = fileInfo.Name;
        var totalBytes = fileInfo.Length;

        try
        {
            DeployRunBtn.IsEnabled = false;
            DeployStatusBadge.Text = "Uploading…";
            AppendDeployLog($"\r\n[{DateTime.Now:HH:mm:ss}] Preparing to deploy '{fileName}' ({totalBytes / 1024.0 / 1024.0:F2} MB) to {_agentInfo?.MachineName}…");

            using var call = _conn!.Client.DeployPackage(deadline: DateTime.UtcNow.AddMinutes(10));
            var buffer = new byte[256 * 1024]; // 256 KB chunks
            long sent = 0;
            var first = true;

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
                    DeployStatusBadge.Text = $"Uploading {pct}%";
                }
            }

            await call.RequestStream.CompleteAsync();
            DeployStatusBadge.Text = "Installing Silently…";
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] Upload complete (100%). Running silent installer on {_agentInfo?.MachineName}…");

            var result = await call.ResponseAsync;
            DeployStatusBadge.Text = result.Success ? $"Installed (Exit {result.ExitCode})" : $"Failed (Exit {result.ExitCode})";
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] Command: {result.CommandLine}");
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] Result : {(result.Success ? "SUCCESS" : "FAILED")} (Exit Code {result.ExitCode})");
            if (!string.IsNullOrWhiteSpace(result.Output))
                AppendDeployLog(result.Output);

            if (result.Success)
                await RefreshInventoryCoreAsync();
        }
        catch (Exception ex)
        {
            DeployStatusBadge.Text = "Deploy Failed";
            AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}");
        }
        finally
        {
            DeployRunBtn.IsEnabled = true;
        }
    }

    private async void OnWingetQuickInstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string packageId }) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(packageId, @"^[a-zA-Z0-9._\-]+$")) return;
        if (_conn is null && !await ConnectToCurrentFieldsAsync()) return;

        // Create a tiny temporary .ps1 script that runs winget silently and deploy it via DeployPackage
        var tempScript = Path.Combine(Path.GetTempPath(), $"winget_{packageId}_{Guid.NewGuid():N}.ps1");
        try
        {
            var scriptBody = $"winget install --id '{packageId}' -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
            await File.WriteAllTextAsync(tempScript, scriptBody);
            await DeployLocalFileCoreAsync(tempScript, "AUTO");
        }
        finally
        {
            try { if (File.Exists(tempScript)) File.Delete(tempScript); } catch { }
        }
    }

    private void AppendDeployLog(string line)
    {
        DeployOutputBox.AppendText(line + "\r\n");
        DeployOutputBox.ScrollToEnd();
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

        await ConnectToCurrentFieldsAsync();
        await Task.Delay(500);
        SaveWindowPng(Path.Combine(outDir, "controller-inventory.png"));

        // Switch to Shell tab, run a real PowerShell command, capture screenshot
        NavShell.IsChecked = true;
        ShellInputBox.Text = "Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 8 Id, ProcessName, @{N='RAM_MB';E={[math]::Round($_.WorkingSet64/1MB,1)}} | Format-Table -AutoSize";
        await SendShellLineAsync();
        await Task.Delay(1400);
        SaveWindowPng(Path.Combine(outDir, "controller-shell.png"));

        // Switch to Remote Desktop tab, capture live frame, capture screenshot
        NavDesktop.IsChecked = true;
        ControlCheck.IsChecked = true;
        _ = StartStreamCoreAsync();
        await Task.Delay(1200);
        SaveWindowPng(Path.Combine(outDir, "controller-desktop.png"));
        StopStream();

        Application.Current.Shutdown(0);
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
    private static string Short(Exception ex) => ex.Message.Split('\n')[0];

    protected override void OnClosed(EventArgs e)
    {
        Disconnect();
        base.OnClosed(e);
    }
}