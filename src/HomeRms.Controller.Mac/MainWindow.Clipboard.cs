using Avalonia.Threading;
using Grpc.Core;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Mac;

/// <summary>
/// Live two-way clipboard while remote control is on (macOS/Linux controller): local copies are
/// pushed to the remote PC (checked twice a second — Avalonia has no clipboard-changed event), and
/// copies made on the remote PC arrive on this computer's clipboard. Works with keyboard shortcuts
/// and right-click Copy/Paste alike.
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? _clipCts;
    private DispatcherTimer? _clipTimer;
    private string? _lastClipFromRemote;
    private string? _lastClipPushed;
    private string? _lastLocalClip;
    private bool _clipBusy;

    private void StartClipboardSync()
    {
        if (_conn is null || _clipCts is not null) return;
        _clipCts = new CancellationTokenSource();
        var token = _clipCts.Token;
        var client = _conn.Client;
        _lastClipPushed = null;
        _lastLocalClip = null; // first tick pushes what is already copied

        _ = Task.Run(() => WatchRemoteClipboardAsync(client, token));

        _clipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clipTimer.Tick += async (_, _) => await PollLocalClipboardAsync();
        _clipTimer.Start();
    }

    private void StopClipboardSync()
    {
        _clipCts?.Cancel();
        _clipCts = null;
        _clipTimer?.Stop();
        _clipTimer = null;
    }

    private async Task PollLocalClipboardAsync()
    {
        if (_clipBusy || _conn is null || _clipCts is null || Clipboard is null) return;
        _clipBusy = true;
        try
        {
            string? text;
            try { text = await Clipboard.GetTextAsync(); }
            catch { return; }

            if (string.IsNullOrEmpty(text) || text == _lastLocalClip) return;
            _lastLocalClip = text;
            if (text == _lastClipFromRemote || text == _lastClipPushed) return;

            _lastClipPushed = text;
            try
            {
                await _conn.Client.SetClipboardAsync(new ClipboardData { Text = text },
                    deadline: DateTime.UtcNow.AddSeconds(5));
                StatusText.Text = $"Clipboard sent to {_agentInfo?.MachineName} ({text.Length} chars) — paste it there.";
            }
            catch (Exception ex)
            {
                _lastClipPushed = null;
                StatusText.Text = "Clipboard send failed: " + Short(ex);
            }
        }
        finally
        {
            _clipBusy = false;
        }
    }

    private async Task WatchRemoteClipboardAsync(RemoteManagement.RemoteManagementClient client, CancellationToken token)
    {
        try
        {
            using var call = client.WatchClipboard(new Empty(), cancellationToken: token);
            await foreach (var data in call.ResponseStream.ReadAllAsync(token))
            {
                var text = data.Text;
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await SetLocalClipboardFromRemoteAsync(text);
                    StatusText.Text = $"Copied from {_agentInfo?.MachineName} ({text.Length} chars) — paste it here.";
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            Dispatcher.UIThread.Post(() =>
                StatusText.Text = "This agent is older: copying from it works with Ctrl/Cmd+C only. Update the agent for right-click copy.");
        }
        catch
        {
            // stream dropped (disconnect / grant revoked)
        }
    }

    private async Task SetLocalClipboardFromRemoteAsync(string text)
    {
        if (string.IsNullOrEmpty(text) || Clipboard is null) return;
        _lastClipFromRemote = text;
        _lastLocalClip = text; // the poller must not send it straight back
        try { await Clipboard.SetTextAsync(text); } catch { }
    }
}
