using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Grpc.Core;
using HomeRms.Controller.Services;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller;

public partial class MainWindow : Window
{
    private RmsConnection? _conn;
    private AgentInfo? _agentInfo;

    // Shell
    private AsyncDuplexStreamingCall<ShellInput, ShellOutput>? _shellCall;
    private readonly SemaphoreSlim _shellWriteLock = new(1, 1);

    // Screen + input
    private CancellationTokenSource? _streamCts;
    private AsyncClientStreamingCall<InputEvent, InputAck>? _inputCall;
    private Channel<InputEvent>? _inputChannel;
    private int _frameWidth = 1920;
    private int _frameHeight = 1080;

    public MainWindow() => InitializeComponent();

    // ---------------- Connection ----------------
    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_conn is not null)
        {
            Disconnect();
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port)) port = 7443;
        try
        {
            StatusText.Text = "Connecting…";
            var conn = new RmsConnection(HostBox.Text.Trim(), port);
            _agentInfo = await conn.Client.GetAgentInfoAsync(new Empty(),
                deadline: DateTime.UtcNow.AddSeconds(10));
            _conn = conn;
            _frameWidth = _agentInfo.PrimaryScreenWidth;
            _frameHeight = _agentInfo.PrimaryScreenHeight;

            Tabs.IsEnabled = true;
            ConnectBtn.Content = "Disconnect";
            StatusText.Text = $"Connected to {_agentInfo.MachineName} ({_agentInfo.LoggedInUser}) — {_agentInfo.OsVersion}";
        }
        catch (Exception ex)
        {
            _conn = null;
            StatusText.Text = "Connection failed: " + Short(ex);
            MessageBox.Show(ex.Message, "Connect failed", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        Tabs.IsEnabled = false;
        ConnectBtn.Content = "Connect";
        StatusText.Text = "Not connected";
    }

    // ---------------- Inventory ----------------
    private async void OnRefreshInventory(object sender, RoutedEventArgs e)
    {
        if (_conn is null) return;
        try
        {
            RefreshInvBtn.IsEnabled = false;
            var inv = await _conn.Client.GetInventoryAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(30));
            var hw = inv.Hardware;
            HardwareText.Text =
                $"{hw.Cpu}  •  {hw.LogicalCores} cores  •  {Gb(hw.TotalRamBytes)} GB RAM  •  {hw.Gpu}  •  " +
                string.Join("  ", hw.Disks.Select(d => $"{d.Name} {Gb(d.FreeBytes)}/{Gb(d.TotalBytes)} GB free"));

            AppsGrid.ItemsSource = inv.Apps.OrderBy(a => a.Name)
                .Select(a => new { a.Name, a.Version, a.Publisher }).ToList();
            ProcGrid.ItemsSource = inv.Processes
                .Select(p => new { p.Pid, p.Name, MemoryMb = p.WorkingSetBytes / (1024 * 1024) }).ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Inventory failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshInvBtn.IsEnabled = true; }
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
            Dispatcher.Invoke(() => ShellOutput.AppendText($"\n[shell closed: {Short(ex)}]\n"));
        }
    }

    private async void OnShellSend(object sender, RoutedEventArgs e) => await SendShellLineAsync();

    private async void OnShellInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SendShellLineAsync(); }
    }

    private async Task SendShellLineAsync()
    {
        if (_conn is null) return;
        var line = ShellInputBox.Text;
        ShellInputBox.Clear();
        await EnsureShellAsync();
        if (_shellCall is null) return;

        ShellOutput.AppendText("> " + line + "\n");
        await _shellWriteLock.WaitAsync();
        try { await _shellCall.RequestStream.WriteAsync(new ShellInput { Data = line }); }
        catch (Exception ex) { ShellOutput.AppendText($"[send failed: {Short(ex)}]\n"); }
        finally { _shellWriteLock.Release(); }
    }

    // ---------------- Remote Desktop ----------------
    private async void OnToggleStream(object sender, RoutedEventArgs e)
    {
        if (_streamCts is not null) { StopStream(); return; }
        if (_conn is null) return;

        _streamCts = new CancellationTokenSource();
        StreamBtn.Content = "Stop viewing";
        StreamStatus.Text = "Requesting consent on the remote machine…";

        if (ControlCheck.IsChecked == true)
            StartInputStream();

        try
        {
            using var call = _conn.Client.StreamScreen(
                new ScreenRequest { Fps = 12, Quality = 60, OperatorName = Environment.UserName },
                cancellationToken: _streamCts.Token);

            await foreach (var frame in call.ResponseStream.ReadAllAsync(_streamCts.Token))
            {
                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                var bytes = frame.Jpeg.ToByteArray();
                Dispatcher.Invoke(() =>
                {
                    StreamStatus.Text = $"Viewing — frame {frame.Sequence} ({frame.Width}×{frame.Height})";
                    ScreenImage.Source = Decode(bytes);
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            Dispatcher.Invoke(() => { StreamStatus.Text = "Declined on the remote machine."; });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => { StreamStatus.Text = "Stream error: " + Short(ex); });
        }
        finally
        {
            Dispatcher.Invoke(StopStream);
        }
    }

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts = null;
        StopInputStream();
        StreamBtn.Content = "Start viewing";
        if (StreamStatus is not null) StreamStatus.Text = "Idle";
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

    // ---------------- Input injection ----------------
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

        // Image uses Stretch=Uniform → letterboxed. Find the rendered content rect.
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

    // ---------------- helpers ----------------
    private static long Gb(long bytes) => bytes / (1024 * 1024 * 1024);
    private static string Short(Exception ex) => ex.Message.Split('\n')[0];

    protected override void OnClosed(EventArgs e)
    {
        Disconnect();
        base.OnClosed(e);
    }
}
