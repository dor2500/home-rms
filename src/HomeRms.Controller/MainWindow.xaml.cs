using System.Collections.ObjectModel;
using System.IO;
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
        if (_machines.Count > 0)
            MachinesList.SelectedIndex = 0;

        ShellOutput.Text =
            "Home RMS Interactive PowerShell Console\r\n" +
            "Connected over Mutual TLS (HTTP/2). Every command executed here is recorded in the remote agent's visible activity log.\r\n" +
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
        if (InventoryPanel is null || DesktopPanel is null || ShellPanel is null) return;

        InventoryPanel.Visibility = NavInventory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DesktopPanel.Visibility = NavDesktop.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShellPanel.Visibility = NavShell.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Machine List & Probing ----------------
    private void OnMachineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MachinesList.SelectedItem is not MachineProfile m) return;
        NameBox.Text = m.Name;
        HostBox.Text = m.Host;
        PortBox.Text = m.Port.ToString();

        if (_conn is null)
        {
            TargetTitleText.Text = m.DisplayTitle;
            StatusText.Text = $"Endpoint {m.Address} • Click Connect to open an authenticated mTLS session.";
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
            SetBadge("Connecting…", Color.FromArgb(45, 245, 158, 11), Color.FromRgb(251, 191, 36));

            var conn = new RmsConnection(host, port);
            _agentInfo = await conn.Client.GetAgentInfoAsync(new Empty(),
                deadline: DateTime.UtcNow.AddSeconds(8));
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

            TargetTitleText.Text = $"{_agentInfo.MachineName} ({host}:{port})";
            StatusText.Text =
                $"Signed-in user: {_agentInfo.LoggedInUser}   •   Display: {_agentInfo.PrimaryScreenWidth}×{_agentInfo.PrimaryScreenHeight}   •   OS: {_agentInfo.OsVersion}";
            SetBadge("Connected", Color.FromArgb(45, 34, 197, 94), Color.FromRgb(74, 222, 128));

            ConnectBtn.Content = "Disconnect";
            ConnectBtn.Style = (Style)FindResource("DangerButton");

            await RefreshInventoryCoreAsync();
            return true;
        }
        catch (Exception ex)
        {
            _conn = null;
            SetBadge("Connection Failed", Color.FromArgb(45, 239, 68, 68), Color.FromRgb(248, 113, 113));
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
        SetBadge("Disconnected", Color.FromArgb(38, 107, 114, 128), Color.FromRgb(156, 163, 175));
        StatusText.Text = "Session closed. Click Connect to start a new session.";
    }

    private void SetBadge(string text, Color bg, Color fg)
    {
        ConnBadgeText.Text = text;
        ConnBadgeBorder.Background = new SolidColorBrush(bg);
        ConnBadgeText.Foreground = new SolidColorBrush(fg);
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
        StreamBtn.Content = "⏹  Stop Viewing";
        StreamBtn.Style = (Style)FindResource("DangerButton");
        StreamStatus.Text = "Awaiting on-machine consent…";

        var fps = int.TryParse(FpsBox.Text, out var f) ? Math.Clamp(f, 1, 30) : 15;
        var quality = int.TryParse(QualityBox.Text, out var q) ? Math.Clamp(q, 20, 95) : 65;

        try
        {
            using var call = _conn.Client.StreamScreen(
                new ScreenRequest { Fps = fps, Quality = quality, OperatorName = Environment.UserName },
                cancellationToken: _streamCts.Token);

            await foreach (var frame in call.ResponseStream.ReadAllAsync(_streamCts.Token))
            {
                if (_inputCall is null && ControlCheck.IsChecked == true)
                    StartInputStream();

                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                var bytes = frame.Jpeg.ToByteArray();
                Dispatcher.Invoke(() =>
                {
                    DesktopOverlay.Visibility = Visibility.Collapsed;
                    StreamStatus.Text = $"LIVE  •  Frame #{frame.Sequence}  •  {frame.Width}×{frame.Height}  •  {bytes.Length / 1024} KB";
                    ScreenImage.Source = Decode(bytes);
                });
            }
        }
        catch (OperationCanceledException) { }
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
            Dispatcher.Invoke(StopStream);
        }
    }

    private void OnControlCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_streamCts is null) return;
        if (ControlCheck.IsChecked == true) StartInputStream();
        else StopInputStream();
    }

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts = null;
        StopInputStream();
        if (StreamBtn is not null)
        {
            StreamBtn.Content = "▶  Start Viewing";
            StreamBtn.Style = (Style)FindResource("PrimaryButton");
        }
        if (StreamStatus is not null && !StreamStatus.Text.StartsWith("Access declined"))
            StreamStatus.Text = "Idle — not streaming";
    }

    private static BitmapImage Decode(byte[] jpeg)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(jpeg);
        img.EndInit();
        img.Freeze();
        return img;
    }

    // ---------------- Input Injection ----------------
    private void StartInputStream()
    {
        if (_conn is null || _inputCall is not null) return;
        _inputCall = _conn.Client.SendInput();
        _inputChannel = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions { SingleReader = true });
        _ = PumpInputAsync(_inputCall, _inputChannel);
        ScreenImage.Focus();
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

    private void OnScreenMouseMove(object sender, MouseEventArgs e)
    {
        if (_inputCall is null) return;
        if (TryMapToScreen(e.GetPosition(ScreenImage), out var x, out var y))
            QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.MouseMove, X = x, Y = y });
    }

    private void OnScreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_inputCall is null) return;
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

    private void OnScreenKeyDown(object sender, KeyEventArgs e)
    {
        if (_inputCall is null) return;
        e.Handled = true;
        QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyDown, KeyCode = KeyInterop.VirtualKeyFromKey(e.Key) });
    }

    private void OnScreenKeyUp(object sender, KeyEventArgs e)
    {
        if (_inputCall is null) return;
        e.Handled = true;
        QueueInput(new InputEvent { Kind = InputEvent.Types.Kind.KeyUp, KeyCode = KeyInterop.VirtualKeyFromKey(e.Key) });
    }

    private static int ButtonCode(MouseButton b) => b switch
    {
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => 0
    };

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
