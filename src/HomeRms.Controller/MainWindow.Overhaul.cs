using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

namespace HomeRms.Controller;

public partial class MainWindow
{
    private readonly AgentlessService _agentless = new();
    private readonly SshApplianceService _ssh = new();
    private readonly AlertCenterService _alerts = new();
    private readonly WingetCatalogService _winget = new();
    private readonly ObservableCollection<WingetAppItem> _filteredWingetApps = [];
    private Window? _detachedMonitorWindow;
    private CancellationTokenSource? _detachedMonitorCts;
    private int _selectedMonitorIndex;

    private MachineProfile? SelectedMachine => MachinesList.SelectedItem as MachineProfile;

    private void InitializeOverhaulServices()
    {
        AlertsListBox.ItemsSource = _alerts.Alerts;
        _alerts.AlertsChanged += RefreshAlertBadge;
        RefreshAlertBadge();

        CpuThresholdBox.Text = _alerts.Thresholds.CpuThresholdPercent.ToString("0");
        RamThresholdBox.Text = _alerts.Thresholds.RamThresholdPercent.ToString("0");
        DiskThresholdBox.Text = _alerts.Thresholds.DiskFreeMinPercent.ToString("0");
        TempThresholdBox.Text = _alerts.Thresholds.TempThresholdC.ToString("0");

        WingetCatalogList.ItemsSource = _filteredWingetApps;
        WingetPackCombo.ItemsSource = _winget.Packs;
        WingetPackCombo.DisplayMemberPath = nameof(WingetPack.Name);
        if (_winget.Packs.Count > 0) WingetPackCombo.SelectedIndex = 0;
        RefreshWingetFilter();
    }

    private void RefreshAlertBadge()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var count = _alerts.UnreadCount;
            AlertBadgeText.Text = count.ToString();
            AlertBadgeBorder.Background = count > 0
                ? new SolidColorBrush(Color.FromArgb(45, 239, 68, 68))
                : new SolidColorBrush(Color.FromArgb(34, 59, 130, 246));
            AlertBadgeText.Foreground = count > 0
                ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
                : (Brush)FindResource("TextBrush");
        });
    }

    private void UpdateDynamicTabVisibility(MachineProfile? m)
    {
        if (m is null)
        {
            NavDesktop.Visibility = Visibility.Visible;
            NavInventory.Visibility = Visibility.Visible;
            NavShell.Visibility = Visibility.Visible;
            NavDeploy.Visibility = Visibility.Visible;
            NavUpdates.Visibility = Visibility.Visible;
            NavSystem.Visibility = Visibility.Visible;
            NavWebUi.Visibility = Visibility.Collapsed;
            LaunchRdpBtn.Visibility = Visibility.Collapsed;
            return;
        }

        if (m.IsAppliance)
        {
            NavDesktop.Visibility = Visibility.Collapsed;
            NavInventory.Visibility = Visibility.Collapsed;
            NavDeploy.Visibility = Visibility.Collapsed;
            NavUpdates.Visibility = Visibility.Collapsed;
            NavSystem.Visibility = Visibility.Collapsed;
            NavShell.Visibility = string.IsNullOrWhiteSpace(m.Username) && m.Kind is EndpointKind.HomeAssistant or EndpointKind.WebService
                ? Visibility.Collapsed : Visibility.Visible;
            NavLabelShell.Text = "SSH Terminal";
            NavWebUi.Visibility = Visibility.Visible;
            WebUiTitleText.Text = $"{m.DisplayTitle} — {m.ModeBadge}";
            WebUiUrlText.Text = $"http://localhost:{_centralHub.Port}/proxy/{m.Host}/";
            LaunchRdpBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            NavDesktop.Visibility = Visibility.Visible;
            NavInventory.Visibility = Visibility.Visible;
            NavShell.Visibility = Visibility.Visible;
            NavLabelShell.Text = "PowerShell";
            NavDeploy.Visibility = Visibility.Visible;
            NavUpdates.Visibility = Visibility.Visible;
            NavSystem.Visibility = Visibility.Visible;
            NavWebUi.Visibility = string.IsNullOrWhiteSpace(m.WebUrl) ? Visibility.Collapsed : Visibility.Visible;
            LaunchRdpBtn.Visibility = m.IsAgentless ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------------- Mobile Hub & Alert Center ----------------
    private void OnOpenMobileHubClick(object sender, RoutedEventArgs e)
    {
        var url = $"http://localhost:{_centralHub.Port}/mobile";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { StatusText.Text = $"Mobile Hub URL: {url}"; }
    }

    private void OnOpenAlertCenterClick(object sender, RoutedEventArgs e)
    {
        AlertDrawerOverlay.Visibility = Visibility.Visible;
    }

    private void OnCloseAlertCenterClick(object sender, RoutedEventArgs e)
    {
        AlertDrawerOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnAckAllAlertsClick(object sender, RoutedEventArgs e) => _alerts.AcknowledgeAll();
    private void OnClearAlertsClick(object sender, RoutedEventArgs e) => _alerts.ClearAll();

    private void OnSaveAlertThresholdsClick(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(CpuThresholdBox.Text, out var cpu)) _alerts.Thresholds.CpuThresholdPercent = Math.Clamp(cpu, 10, 100);
        if (double.TryParse(RamThresholdBox.Text, out var ram)) _alerts.Thresholds.RamThresholdPercent = Math.Clamp(ram, 10, 100);
        if (double.TryParse(DiskThresholdBox.Text, out var dsk)) _alerts.Thresholds.DiskFreeMinPercent = Math.Clamp(dsk, 1, 90);
        if (double.TryParse(TempThresholdBox.Text, out var tmp)) _alerts.Thresholds.TempThresholdC = Math.Clamp(tmp, 40, 115);
        _alerts.SaveThresholds();
        AddActivity("ALERT", "Alert Center thresholds saved.");
        StatusText.Text = "Alert thresholds updated.";
    }

    // ---------------- Multi-Monitor 3 Modes & Agentless RDP ----------------
    private async void OnQuickMonitorSwitchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out var idx)) return;
        _selectedMonitorIndex = idx;
        if (idx >= 0 && idx < MonitorCombo.Items.Count)
            MonitorCombo.SelectedIndex = idx;

        if (_streamCts is not null)
        {
            StopStream();
            await Task.Delay(120);
            await StartStreamCoreAsync();
        }
        StatusText.Text = idx == -1 ? "All-in-One multi-monitor view selected." : $"Switched to Display {idx + 1}.";
    }

    private void OnDetachSecondMonitorClick(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine is { IsAgentless: true } am)
        {
            _agentless.LaunchRdpSession(am, multimon: true);
            return;
        }

        if (_detachedMonitorWindow is not null)
        {
            _detachedMonitorCts?.Cancel();
            _detachedMonitorWindow.Close();
            _detachedMonitorWindow = null;
            return;
        }

        if (_conn is null)
        {
            StatusText.Text = "Connect to an Agent endpoint first to detach Screen 2.";
            return;
        }

        var hasSecondMonitor = SystemParameters.VirtualScreenWidth > SystemParameters.PrimaryScreenWidth + 100;
        var img = new Image { Stretch = Stretch.Uniform };
        var win = new Window
        {
            Title = $"Home RMS — Screen 2 ({SelectedMachine?.DisplayTitle})",
            Background = Brushes.Black,
            Width = 1280,
            Height = 720,
            Left = hasSecondMonitor ? SystemParameters.PrimaryScreenWidth + 40 : 80,
            Top = 40,
            WindowStyle = hasSecondMonitor ? WindowStyle.None : WindowStyle.SingleBorderWindow,
            WindowState = hasSecondMonitor ? WindowState.Maximized : WindowState.Normal,
            Content = img,
        };
        var cts = new CancellationTokenSource();
        _detachedMonitorCts = cts;
        _detachedMonitorWindow = win;
        win.Closed += (_, _) => { cts.Cancel(); _detachedMonitorWindow = null; };
        win.Show();

        var conn = _conn;
        _ = Task.Run(async () =>
        {
            try
            {
                using var call = conn.Client.StreamScreen(new ScreenRequest
                {
                    Fps = 24,
                    Quality = 85,
                    MaxWidth = 0,
                    SupportsTiles = false,
                    MonitorIndex = 1,
                }, cancellationToken: cts.Token);

                while (await call.ResponseStream.MoveNext(cts.Token))
                {
                    var bytes = call.ResponseStream.Current.Jpeg.ToByteArray();
                    if (bytes.Length == 0) continue;
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        using var ms = new MemoryStream(bytes);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                        img.Source = bmp;
                    });
                }
            }
            catch { }
        }, cts.Token);
    }

    private void OnLaunchAgentlessRdpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine is not { IsAgentless: true } m) return;
        _agentless.LaunchRdpSession(m, m.UseRdpMultimon);
        AddActivity("RDP", $"Launched RDP session to {m.Host} (multimon={m.UseRdpMultimon})");
    }

    // ---------------- Endpoint Drawer: Dual Mode / Appliances / Prep Script ----------------
    private void OnEndpointKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointKindCombo?.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag?.ToString() ?? "WindowsAgent";
        var isAgent = tag == "WindowsAgent";
        var isAgentless = tag == "WindowsAgentless";
        var isAppliance = !isAgent && !isAgentless;

        if (CredentialsPanel is not null)
            CredentialsPanel.Visibility = isAgent ? Visibility.Collapsed : Visibility.Visible;
        if (SshFieldsGrid is not null)
            SshFieldsGrid.Visibility = tag is "Linux" or "Nas" ? Visibility.Visible : Visibility.Collapsed;
        if (WebUrlPanel is not null)
            WebUrlPanel.Visibility = isAppliance ? Visibility.Visible : Visibility.Collapsed;
        if (AgentlessActionsGrid is not null)
            AgentlessActionsGrid.Visibility = isAgentless ? Visibility.Visible : Visibility.Collapsed;
        if (UseMultimonCheck is not null)
            UseMultimonCheck.Visibility = isAgentless ? Visibility.Visible : Visibility.Collapsed;

        if (PortBox is not null)
        {
            if (isAgentless && PortBox.Text == "7443") PortBox.Text = "5985";
            else if (tag == "HomeAssistant" && PortBox.Text == "7443") PortBox.Text = "8123";
            else if (tag == "Linux" && PortBox.Text == "7443") PortBox.Text = "22";
            else if (isAgent && PortBox.Text is "5985" or "8123" or "22") PortBox.Text = "7443";
        }
    }

    private void OnCopyPrepScriptClick(object sender, RoutedEventArgs e)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "Prepare-AgentlessHost.ps1"));
        var cmd = File.Exists(path)
            ? $"powershell -ExecutionPolicy Bypass -File \"{path}\""
            : "irm https://raw.githubusercontent.com/dor2500/home-rms/master/tools/Prepare-AgentlessHost.ps1 | iex";
        Clipboard.SetText(cmd);
        StatusText.Text = "Copied Agentless Prep Script command to clipboard.";
    }

    private async void OnInstallAgentRemotelyClick(object sender, RoutedEventArgs e)
    {
        var m = BuildProfileFromDrawer();
        if (string.IsNullOrWhiteSpace(m.Host) || string.IsNullOrWhiteSpace(m.Username))
        {
            StatusText.Text = "Enter Host and WinRM Admin Username/Password first.";
            return;
        }
        StatusText.Text = $"Installing Home RMS Agent remotely on {m.Host}...";
        var fp = AboutFingerprintBox?.Text ?? "";
        var (ok, msg) = await _agentless.InstallAgentRemotelyAsync(m, fp, line => AddActivity("DEPLOY", line));
        StatusText.Text = msg;
        if (ok)
        {
            EndpointKindCombo.SelectedIndex = 0; // Switch to Agent (mTLS)
            PortBox.Text = "7443";
        }
    }

    private MachineProfile BuildProfileFromDrawer()
    {
        var tag = (EndpointKindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "WindowsAgent";
        var kind = Enum.TryParse<EndpointKind>(tag, out var k) ? k : EndpointKind.WindowsAgent;
        var p = new MachineProfile
        {
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : 7443,
            MacAddress = MacBox.Text.Trim(),
            Kind = kind,
            Username = UsernameBox.Text.Trim(),
            SshPort = int.TryParse(SshPortBox.Text.Trim(), out var sp) ? sp : 22,
            SshKeyPath = SshKeyBox.Text.Trim(),
            WebUrl = WebUrlBox.Text.Trim(),
            UseRdpMultimon = UseMultimonCheck.IsChecked == true,
        };
        if (!string.IsNullOrEmpty(EndpointPasswordBox.Password))
            p.SetPlainPassword(EndpointPasswordBox.Password);
        return p;
    }

    // ---------------- Winstall-Style Visual Software Catalog ----------------
    private void RefreshWingetFilter()
    {
        var q = WingetSearchBox?.Text.Trim() ?? "";
        _filteredWingetApps.Clear();
        foreach (var app in _winget.Catalog)
        {
            if (string.IsNullOrEmpty(q) ||
                app.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                app.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                app.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                app.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                _filteredWingetApps.Add(app);
            }
        }
        UpdateWingetSelectedCount();
    }

    private void UpdateWingetSelectedCount()
    {
        if (WingetDeploySelectedBtn is null) return;
        var n = _winget.Catalog.Count(a => a.IsSelected);
        WingetDeploySelectedBtn.Content = $"Deploy Selected ({n})";
    }

    private void OnWingetSearchChanged(object sender, TextChangedEventArgs e) => RefreshWingetFilter();

    private async void OnWingetLiveSearchClick(object sender, RoutedEventArgs e)
    {
        var q = WingetSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(q)) return;
        DeployStatusBadge.Text = "SEARCHING...";
        var added = await _winget.RefreshFromWingetAsync(q);
        RefreshWingetFilter();
        DeployStatusBadge.Text = added > 0 ? $"+{added} FOUND" : "READY";
    }

    private void OnWingetCardSelectionChanged(object sender, RoutedEventArgs e) => UpdateWingetSelectedCount();

    private void OnApplyWingetPackClick(object sender, RoutedEventArgs e)
    {
        if (WingetPackCombo.SelectedItem is not WingetPack pack) return;
        foreach (var app in _winget.Catalog)
            app.IsSelected = pack.PackageIds.Any(id => id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
        UpdateWingetSelectedCount();
        DeployOutputBox.AppendText($"[pack] Loaded preset '{pack.Name}' ({pack.PackageIds.Count} apps).\r\n");
    }

    private void OnSaveWingetPackClick(object sender, RoutedEventArgs e)
    {
        var selected = _winget.Catalog.Where(a => a.IsSelected).Select(a => a.Id).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "Select at least one app before saving a Pack.";
            return;
        }
        var name = $"Custom Pack ({DateTime.Now:HHmm})";
        _winget.SaveCustomPack(name, $"{selected.Count} selected applications", selected);
        WingetPackCombo.ItemsSource = null;
        WingetPackCombo.ItemsSource = _winget.Packs;
        WingetPackCombo.SelectedIndex = _winget.Packs.Count - 1;
        StatusText.Text = $"Saved '{name}' with {selected.Count} apps.";
    }

    private void OnImportWingetIdsClick(object sender, RoutedEventArgs e)
    {
        var clip = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(clip))
        {
            StatusText.Text = "Copy Winget IDs (one per line) or a winstall JSON to clipboard first.";
            return;
        }
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(clip);
            if (doc.RootElement.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in apps.EnumerateArray())
                    if (a.TryGetProperty("id", out var idEl) && idEl.GetString() is { } s) ids.Add(s);
            }
        }
        catch
        {
            ids.AddRange(clip.Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries));
        }

        var matched = 0;
        foreach (var id in ids)
        {
            var found = _winget.Catalog.FirstOrDefault(a => a.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found is not null) { found.IsSelected = true; matched++; }
        }
        UpdateWingetSelectedCount();
        StatusText.Text = $"Imported & selected {matched} matching Winget packages from clipboard.";
    }

    private async void OnDeploySelectedWingetClick(object sender, RoutedEventArgs e)
    {
        var queue = _winget.Catalog.Where(a => a.IsSelected).ToList();
        if (queue.Count == 0)
        {
            StatusText.Text = "Select at least one application card to deploy.";
            return;
        }

        var silent = WingetSilentCheck.IsChecked == true;
        var machineScope = WingetMachineScopeCheck.IsChecked == true;
        var upgrade = WingetUpgradeCheck.IsChecked == true;

        DeployStatusBadge.Text = "DEPLOYING...";
        WingetDeploySelectedBtn.IsEnabled = false;
        try
        {
            foreach (var app in queue)
            {
                app.DeployStatus = DeployItemStatus.Installing;
                DeployOutputBox.AppendText($"[winget] Installing {app.Name} ({app.Id})...\r\n");
                var ps = WingetCatalogService.BuildWingetInstallScript(app.Id, silent, machineScope, upgrade);

                bool ok;
                string output;
                if (SelectedMachine is { IsAgentless: true } agentlessHost)
                {
                    (ok, output) = await _agentless.RunShellAsync(agentlessHost, ps);
                }
                else if (_conn is not null)
                {
                    using var call = _conn.Client.Shell();
                    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
                    await call.RequestStream.WriteAsync(new ShellInput { Data = $"powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}\n" });
                    await call.RequestStream.WriteAsync(new ShellInput { Close = true });
                    await call.RequestStream.CompleteAsync();
                    var sb = new StringBuilder();
                    while (await call.ResponseStream.MoveNext(CancellationToken.None))
                        sb.Append(call.ResponseStream.Current.Data);
                    output = sb.ToString();
                    ok = !output.Contains("No package found", StringComparison.OrdinalIgnoreCase) &&
                         !output.Contains("Failed", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    app.DeployStatus = DeployItemStatus.Failed;
                    DeployOutputBox.AppendText("[error] Connect to an Agent or select an Agentless endpoint first.\r\n");
                    break;
                }

                app.DeployStatus = ok ? DeployItemStatus.Success : DeployItemStatus.Failed;
                DeployOutputBox.AppendText(output + "\r\n");
                DeployOutputBox.ScrollToEnd();
            }
            DeployStatusBadge.Text = "COMPLETE";
        }
        finally
        {
            WingetDeploySelectedBtn.IsEnabled = true;
        }
    }

    // ---------------- Reverse-Proxied Web UI & Appliance Health ----------------
    private void OnOpenProxiedWebUiClick(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine is null) return;
        var url = $"http://localhost:{_centralHub.Port}/proxy/{SelectedMachine.Host}/";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void OnRefreshApplianceHealthClick(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine is null) return;
        ApplianceMetricsText.Text = $"Probing {SelectedMachine.Host}...";
        var m = await _ssh.ProbeAsync(SelectedMachine);
        SelectedMachine.IsOnline = m.Online;
        SelectedMachine.LatencyMs = m.LatencyMs;
        SelectedMachine.CpuPercent = m.CpuPercent;
        SelectedMachine.RamPercent = m.RamPercent;
        SelectedMachine.StatusLabel = m.StatusText;
        ApplianceMetricsText.Text =
            $"Status:   {m.StatusText} ({m.LatencyMs} ms)\n" +
            $"CPU:      {m.CpuPercent:0.0}%\n" +
            $"RAM:      {m.RamPercent:0.0}%\n" +
            $"Disk:     {m.DiskSummary}\n" +
            $"Services: {m.ServicesSummary}";
    }

    // ---------------- Optional 2FA Backup Codes ----------------
    private void OnRegenerateBackupCodesClick(object sender, RoutedEventArgs e)
    {
        var cfg = TwoFactorStore.LoadController() with
        {
            BackupCodes = TwoFactorStore.GenerateBackupCodes(),
            RememberDays = int.TryParse(RememberDaysBox.Text, out var d) ? Math.Clamp(d, 1, 365) : 30,
        };
        TwoFactorStore.SaveController(cfg);
        AboutBackupCodesBox.Text = string.Join("   ", cfg.BackupCodes ?? []);
        StatusText.Text = "Generated 8 new one-time 2FA backup codes.";
    }
}
