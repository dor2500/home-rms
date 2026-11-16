using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Grpc.Core;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller;

/// <summary>
/// Live two-way clipboard while remote control is on, so copy/paste works however it is done
/// (Ctrl+C/V, right-click → Copy/Paste, menus): local copies are pushed to the remote PC as they
/// happen, and copies on the remote PC arrive on this PC's clipboard.
/// </summary>
public partial class MainWindow
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private CancellationTokenSource? _clipCts;
    private bool _clipListenerAdded;
    private string? _lastClipFromRemote;
    private string? _lastClipPushed;

    private void StartClipboardSync()
    {
        if (_conn is null || _clipCts is not null) return;
        EnsureClipboardListener();

        _clipCts = new CancellationTokenSource();
        var token = _clipCts.Token;
        var client = _conn.Client;
        _lastClipPushed = null;

        _ = Task.Run(() => WatchRemoteClipboardAsync(client, token));
        _ = PushLocalClipboardAsync(); // what you already copied is ready to paste over there
    }

    private void StopClipboardSync()
    {
        _clipCts?.Cancel();
        _clipCts = null;
    }

    private void EnsureClipboardListener()
    {
        if (_clipListenerAdded) return;
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        source.AddHook(ClipboardWndProc);
        _clipListenerAdded = AddClipboardFormatListener(source.Handle);
    }

    private IntPtr ClipboardWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _clipCts is not null)
            _ = PushLocalClipboardAsync();
        return IntPtr.Zero;
    }

    /// <summary>Sends this PC's clipboard text to the remote PC (skips echoes and repeats).</summary>
    private async Task PushLocalClipboardAsync()
    {
        if (_conn is null || _clipCts is null) return;
        var text = TryGetLocalClipboardText();
        if (string.IsNullOrEmpty(text) || text == _lastClipFromRemote || text == _lastClipPushed) return;

        _lastClipPushed = text;
        try
        {
            await _conn.Client.SetClipboardAsync(new ClipboardData { Text = text },
                deadline: DateTime.UtcNow.AddSeconds(5));
            StatusText.Text = $"Clipboard sent to {_agentInfo?.MachineName} ({text.Length} chars) — paste it there with Ctrl+V or right-click → Paste.";
        }
        catch (Exception ex)
        {
            _lastClipPushed = null;
            StatusText.Text = "Clipboard send failed: " + Short(ex);
        }
    }

    /// <summary>Receives every copy made on the remote PC and puts it on this PC's clipboard.</summary>
    private async Task WatchRemoteClipboardAsync(RemoteManagement.RemoteManagementClient client, CancellationToken token)
    {
        try
        {
            using var call = client.WatchClipboard(new Empty(), cancellationToken: token);
            await foreach (var data in call.ResponseStream.ReadAllAsync(token))
            {
                var text = data.Text;
                await Dispatcher.InvokeAsync(() =>
                {
                    SetLocalClipboardFromRemote(text);
                    StatusText.Text = $"Copied from {_agentInfo?.MachineName} ({text.Length} chars) — paste it here with Ctrl+V or right-click → Paste.";
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            await Dispatcher.InvokeAsync(() =>
                StatusText.Text = "This agent is older: copying from it works with Ctrl+C only. Update the agent for right-click copy.");
        }
        catch (Exception)
        {
            // Stream dropped (disconnect, grant revoked) — control mode restarts it
        }
    }

    private void SetLocalClipboardFromRemote(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _lastClipFromRemote = text; // so the clipboard listener does not send it straight back
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(40); // another app has the clipboard open
            }
        }
    }

    private static string? TryGetLocalClipboardText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
}
