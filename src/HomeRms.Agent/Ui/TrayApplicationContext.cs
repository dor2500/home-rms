using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HomeRms.Agent.Core;
using Microsoft.Win32;

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
        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) => SetStartupEnabled(startupItem.Checked);
        menu.Items.Add(startupItem);
        menu.Items.Add("About Home RMS Agent", null, (_, _) => ShowAbout());
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

    private static void ShowAbout()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var verStr = $"v{ver.Major}.{ver.Minor}.{ver.Build}";

        using var dlg = new Form
        {
            Text = "About Home RMS Agent",
            ClientSize = new Size(420, 220),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
            Icon = Theme.LoadAppIcon()
        };
        Theme.ApplyWindowChrome(dlg);

        var title = new Label
        {
            Text = "Home RMS Endpoint Agent",
            Font = new Font(Theme.UiFont.FontFamily, 14f, FontStyle.Bold),
            ForeColor = Theme.Text,
            Location = new Point(24, 22),
            AutoSize = true
        };
        var version = new Label
        {
            Text = $"Version: {verStr} (.NET 11.0 Self-Contained)",
            ForeColor = Theme.Muted,
            Location = new Point(24, 56),
            AutoSize = true
        };
        var repoLabel = new Label
        {
            Text = "Project Repository:",
            ForeColor = Theme.Muted,
            Location = new Point(24, 92),
            AutoSize = true
        };
        var link = new LinkLabel
        {
            Text = "https://github.com/dor2500/home-rms",
            LinkColor = Theme.Accent,
            ActiveLinkColor = Color.White,
            VisitedLinkColor = Theme.Accent,
            Location = new Point(24, 114),
            AutoSize = true
        };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(link.Text) { UseShellExecute = true }); } catch { }
        };

        var closeBtn = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Size = new Size(96, 32),
            Location = new Point(300, 164)
        };
        closeBtn.FlatAppearance.BorderSize = 0;

        dlg.Controls.AddRange([title, version, repoLabel, link, closeBtn]);
        dlg.AcceptButton = closeBtn;
        dlg.ShowDialog();
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            return key?.GetValue("HomeRmsAgent") is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    private static void SetStartupEnabled(bool enable)
    {
        try
        {
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return;
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                key.SetValue("HomeRmsAgent", $"\"{exe}\"");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key?.DeleteValue("HomeRmsAgent", throwOnMissingValue: false);
            }
        }
        catch { }
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
