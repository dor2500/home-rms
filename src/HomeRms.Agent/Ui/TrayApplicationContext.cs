using System.Threading;
using System.Windows.Forms;
using HomeRms.Agent.Core;

namespace HomeRms.Agent.Ui;

/// <summary>
/// Owns the agent's UI lifetime: the main status window, the tray icon, the
/// consent prompt, and the "being viewed" banner. Implements <see cref="IConsentUi"/>
/// so the gRPC layer can prompt and signal the user on the UI thread.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext, IConsentUi
{
    private readonly ConsentCoordinator _consent;
    private readonly NotifyIcon _tray;
    private readonly MainForm _form;
    private readonly ViewingBanner _banner;
    private readonly SynchronizationContext _ui;
    private readonly Icon _idleIcon;
    private readonly Icon _activeIcon;
    private readonly bool _autoConsent;
    private readonly string? _screenshotPath;

    public TrayApplicationContext(
        ConsentCoordinator consent,
        ActivityLog log,
        int port,
        bool autoConsent = false,
        string? screenshotPath = null,
        bool showWindowOnStartup = false)
    {
        _consent = consent;
        _autoConsent = autoConsent;
        _screenshotPath = screenshotPath;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _idleIcon = Theme.CreateDot(Theme.Accent);
        _activeIcon = Theme.CreateDot(Theme.Danger);

        _form = new MainForm(log, port);
        _form.RevokeRequested += () => _consent.RevokeAll();
        _form.ExitRequested += () => ExitThread();

        _banner = new ViewingBanner(onStop: () => _consent.RevokeAll());

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Home RMS", null, (_, _) => ShowMain());
        menu.Items.Add("Stop any active session", null, (_, _) => _consent.RevokeAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit agent", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Visible = true,
            Text = "Home RMS — idle",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        _consent.AttachUi(this);

        if (showWindowOnStartup || !string.IsNullOrWhiteSpace(_screenshotPath))
        {
            _form.Show();
        }
    }

    private void ShowMain()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    // --- IConsentUi (marshalled to the UI thread) ---
    public bool AskControlConsent(string operatorName, string action)
    {
        if (_autoConsent) return true;
        var result = false;
        _ui.Send(_ => result = ConsentPrompt.Show(operatorName, action), null);
        return result;
    }

    public void SetViewingActive(bool active, string? operatorName)
    {
        _ui.Post(_ =>
        {
            _tray.Icon = active ? _activeIcon : _idleIcon;
            _tray.Text = active ? $"Home RMS — {operatorName} is viewing" : "Home RMS — idle";
            _form.SetSession(active ? operatorName : null);
            if (active) _banner.ShowFor(operatorName ?? "operator");
            else _banner.Hide();

            if (active && !string.IsNullOrWhiteSpace(_screenshotPath))
            {
                try { _form.SaveScreenshot(_screenshotPath); } catch { }
            }
        }, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _banner.Dispose();
            _form.Dispose();
        }
        base.Dispose(disposing);
    }
}
