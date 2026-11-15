using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using HomeRms.Agent.Core;

namespace HomeRms.Agent.Ui;

/// <summary>
/// Owns the tray presence that keeps the agent visible: a notification icon, the
/// activity-log window, the consent prompt, and the "being viewed" banner. It
/// implements <see cref="IConsentUi"/> so the gRPC layer can prompt and signal
/// the user, always on the UI thread.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext, IConsentUi
{
    private readonly ConsentCoordinator _consent;
    private readonly ActivityLog _log;
    private readonly NotifyIcon _tray;
    private readonly LogWindow _logWindow;
    private readonly ViewingBanner _banner;
    private readonly SynchronizationContext _ui;
    private readonly Icon _iconIdle;
    private readonly Icon _iconActive;

    public TrayApplicationContext(ConsentCoordinator consent, ActivityLog log)
    {
        _consent = consent;
        _log = log;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _iconIdle = MakeIcon(Color.FromArgb(70, 130, 180));
        _iconActive = MakeIcon(Color.FromArgb(200, 40, 40));

        _logWindow = new LogWindow(log);
        _banner = new ViewingBanner(onStop: () => { _consent.RevokeAll(); SetViewingActive(false, null); });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show activity log…", null, (_, _) => ShowLog());
        menu.Items.Add("Revoke all access now", null, (_, _) => _consent.RevokeAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("About Home RMS", null, (_, _) => MessageBox.Show(
            "Home RMS agent\n\nConsent-based remote administration for machines you own.\n" +
            "A banner is always shown while your screen is viewed, and every action is logged.",
            "About Home RMS", MessageBoxButtons.OK, MessageBoxIcon.Information));
        menu.Items.Add("Exit agent", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = _iconIdle,
            Visible = true,
            Text = "Home RMS agent — idle",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowLog();

        _consent.AttachUi(this);
        _tray.ShowBalloonTip(4000, "Home RMS agent", "Running. Your screen is private until you allow a session.", ToolTipIcon.Info);
    }

    private void ShowLog()
    {
        _logWindow.Show();
        _logWindow.WindowState = FormWindowState.Normal;
        _logWindow.BringToFront();
    }

    // --- IConsentUi (marshalled to UI thread) ---
    public bool AskControlConsent(string operatorName, string action)
    {
        var result = false;
        _ui.Send(_ => result = ConsentPrompt.Show(operatorName, action), null);
        return result;
    }

    public void SetViewingActive(bool active, string? operatorName)
    {
        _ui.Post(_ =>
        {
            _tray.Icon = active ? _iconActive : _iconIdle;
            _tray.Text = active ? $"Home RMS — '{operatorName}' is viewing" : "Home RMS agent — idle";
            if (active) _banner.ShowFor(operatorName ?? "operator");
            else _banner.Hide();
        }, null);
    }

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 14, 14);
            using var pen = new Pen(Color.White, 2);
            g.DrawEllipse(pen, 4, 4, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _banner.Dispose();
            _logWindow.Dispose();
        }
        base.Dispose(disposing);
    }
}
