using System.ComponentModel;
using System.Drawing;
using System.Windows;
using HomeRms.Controller.Services;
using WinForms = System.Windows.Forms;

namespace HomeRms.Controller;

public partial class MainWindow
{
    private WinForms.NotifyIcon? _trayIcon;
    private bool _forceExit;
    private bool _shownTrayHint;
    private WindowState _restoreWindowState = WindowState.Normal;

    private void InitializeTrayIcon()
    {
        if (IsScreenshotRun || _trayIcon is not null) return;

        Icon icon;
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/controller.ico"));
            icon = streamInfo is not null ? new Icon(streamInfo.Stream) : SystemIcons.Application;
        }
        catch
        {
            icon = SystemIcons.Application;
        }

        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Open Home RMS Console")
        {
            Font = new Font(menu.Font, System.Drawing.FontStyle.Bold)
        };
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);

        var alertsItem = new WinForms.ToolStripMenuItem("Open Alert Center");
        alertsItem.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RestoreFromTray();
            AlertDrawerOverlay.Visibility = Visibility.Visible;
        });

        var exitItem = new WinForms.ToolStripMenuItem("Exit Home RMS");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(ExitFromTray);

        menu.Items.Add(openItem);
        menu.Items.Add(alertsItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "Home RMS — Central Operations Console",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RestoreFromTray();
            AlertDrawerOverlay.Visibility = Visibility.Visible;
        });

        StateChanged += OnWindowStateChangedForTray;
        Closing += OnWindowClosingForTray;
        Closed += (_, _) =>
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        };
    }

    private void OnWindowStateChangedForTray(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (_ui.MinimizeToTray && !IsScreenshotRun)
                HideToTray();
        }
        else
        {
            _restoreWindowState = WindowState;
        }
    }

    private void OnWindowClosingForTray(object? sender, CancelEventArgs e)
    {
        if (_forceExit || IsScreenshotRun || !_ui.CloseToTray) return;
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        if (!_shownTrayHint && _trayIcon is not null)
        {
            _shownTrayHint = true;
            _trayIcon.ShowBalloonTip(
                3000,
                "Home RMS is running in the background",
                "Monitoring endpoints and real-time alerts. Double-click the tray icon to reopen.",
                WinForms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = _restoreWindowState == WindowState.Minimized ? WindowState.Normal : _restoreWindowState;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitFromTray()
    {
        _forceExit = true;
        Close();
    }

    private void ShowTrayAlertBalloon(AlertItem item)
    {
        if (_trayIcon is null) return;
        var isHidden = !IsVisible || WindowState == WindowState.Minimized;
        if (!isHidden) return;

        var tipIcon = item.Severity switch
        {
            AlertSeverity.Critical => WinForms.ToolTipIcon.Error,
            AlertSeverity.Warning => WinForms.ToolTipIcon.Warning,
            _ => WinForms.ToolTipIcon.Info,
        };
        _trayIcon.ShowBalloonTip(5000, $"{item.Title} — {item.EndpointName}", item.Detail, tipIcon);
    }
}
