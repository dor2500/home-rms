using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HomeRms.Agent.Core;
using HomeRms.Shared.Security;
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
    private volatile bool _autoConsent;
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
        _autoConsent = autoConsent || IsAutoApproveEnabled();
        _screenshotPath = screenshotPath;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _idleIcon = Theme.CreateDot(Theme.Accent);
        _activeIcon = Theme.CreateDot(Theme.Danger);

        _form = new MainForm(log, port, _autoConsent);
        _form.RevokeRequested += () => _consent.RevokeAll();
        _form.ExitRequested += () => ExitThread();

        _banner = new ViewingBanner(onStop: () => _consent.RevokeAll());

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Home RMS", null, (_, _) => ShowMain());
        menu.Items.Add("Stop any active session", null, (_, _) => _consent.RevokeAll());
        menu.Items.Add(new ToolStripSeparator());
        var autoApproveItem = new ToolStripMenuItem("Auto-approve connections (no prompt)")
        {
            Checked = _autoConsent,
            CheckOnClick = true
        };
        autoApproveItem.CheckedChanged += (_, _) =>
        {
            _autoConsent = autoApproveItem.Checked;
            SetAutoApproveEnabled(_autoConsent);
            _form.SetAutoApproveChecked(_autoConsent);
        };
        _form.AutoApproveChanged += enabled =>
        {
            _autoConsent = enabled;
            SetAutoApproveEnabled(enabled);
            if (autoApproveItem.Checked != enabled) autoApproveItem.Checked = enabled;
        };
        menu.Items.Add(autoApproveItem);
        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) => SetStartupEnabled(startupItem.Checked);
        menu.Items.Add(startupItem);
        menu.Items.Add("🔐 2FA Security Setup", null, (_, _) => Show2FaDialog());
        menu.Items.Add("Security: Reset Pinned Controllers", null, (_, _) => ResetPinning());
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

    private static string AutoApproveFlagPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HomeRMS", "agent-auto-approve.flag");

    private static bool IsAutoApproveEnabled() => File.Exists(AutoApproveFlagPath);

    private static void SetAutoApproveEnabled(bool enable)
    {
        try
        {
            if (enable)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AutoApproveFlagPath)!);
                File.WriteAllText(AutoApproveFlagPath, "1");
            }
            else if (File.Exists(AutoApproveFlagPath))
            {
                File.Delete(AutoApproveFlagPath);
            }
        }
        catch { }
    }

    internal static void Show2FaDialog()
    {
        var cfg = TwoFactorStore.LoadAgent();
        using var dlg = new Form
        {
            Text = "Home RMS Agent — 2FA Security",
            ClientSize = new Size(560, 320),
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

        static Image LoadQr(string secret)
        {
            var uri = TotpHelper.BuildOtpAuthUri("HomeRMS-Agent", Environment.MachineName, secret);
            var png = TotpHelper.GenerateQrPng(uri, 5);
            using var ms = new MemoryStream(png);
            return Image.FromStream(ms);
        }

        var qrBox = new PictureBox
        {
            Image = LoadQr(cfg.TotpSecret),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Padding = new Padding(4),
            Size = new Size(150, 150),
            Location = new Point(386, 56)
        };

        var reqCheck = new CheckBox
        {
            Text = "Require 2FA code for initial Controller pairing",
            Checked = cfg.RequireOnStartupOrPair,
            ForeColor = Theme.Text,
            Location = new Point(22, 20),
            AutoSize = true
        };
        reqCheck.CheckedChanged += (_, _) =>
        {
            cfg = cfg with { RequireOnStartupOrPair = reqCheck.Checked };
            TwoFactorStore.SaveAgent(cfg);
        };

        var codeLabel = new Label
        {
            Text = TotpHelper.ComputeCode(cfg.TotpSecret),
            Font = new Font("Cascadia Mono", 26f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Location = new Point(22, 58),
            AutoSize = true
        };
        var timerLabel = new Label
        {
            Text = $"Refreshes in {TotpHelper.SecondsRemaining()}s",
            ForeColor = Theme.Muted,
            Location = new Point(22, 112),
            AutoSize = true
        };

        var secLabel = new Label
        {
            Text = "Authenticator Secret (Base32):",
            ForeColor = Theme.Muted,
            Location = new Point(22, 146),
            AutoSize = true
        };
        var secBox = new TextBox
        {
            Text = cfg.TotpSecret,
            ReadOnly = true,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(22, 168),
            Width = 344,
            Font = Theme.MonoFont
        };

        var copyUriBtn = Theme.Flat("Copy Authenticator URI", Theme.Card, Theme.Text, (_, _) =>
        {
            Clipboard.SetText(TotpHelper.BuildOtpAuthUri("HomeRMS-Agent", Environment.MachineName, cfg.TotpSecret));
        }, 175);
        copyUriBtn.Location = new Point(22, 212);

        var regenBtn = Theme.Flat("Regenerate Secret", Theme.Danger, Color.White, (_, _) =>
        {
            cfg = cfg with { TotpSecret = TotpHelper.GenerateSecretBase32() };
            TwoFactorStore.SaveAgent(cfg);
            secBox.Text = cfg.TotpSecret;
            codeLabel.Text = TotpHelper.ComputeCode(cfg.TotpSecret);
            qrBox.Image?.Dispose();
            qrBox.Image = LoadQr(cfg.TotpSecret);
        }, 155);
        regenBtn.Location = new Point(210, 212);

        var closeBtn = Theme.Flat("Close", Theme.Accent, Color.White, (_, _) => dlg.Close(), 110);
        closeBtn.Location = new Point(426, 265);

        using var tick = new System.Windows.Forms.Timer { Interval = 1000 };
        tick.Tick += (_, _) =>
        {
            codeLabel.Text = TotpHelper.ComputeCode(cfg.TotpSecret);
            timerLabel.Text = $"Refreshes in {TotpHelper.SecondsRemaining()}s";
        };
        tick.Start();

        dlg.Controls.AddRange([reqCheck, codeLabel, timerLabel, secLabel, secBox, qrBox, copyUriBtn, regenBtn, closeBtn]);
        dlg.ShowDialog();
    }

    private static void ResetPinning()
    {
        var pins = TrustedControllerStore.Load();
        var count = pins.Count;
        if (pins.Any(p => p.Sha256 == TrustedControllerStore.UnreadableStoreMarker))
        {
            if (MessageBox.Show($"The pinned-Controller store is damaged ({TrustedControllerStore.LastLoadError}).\n" +
                                "Every Controller is refused until it is reset. Reset it now? You will need to re-pair your Controller.",
                    "Home RMS Security", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                TrustedControllerStore.ClearAll();
            return;
        }
        if (count == 0)
        {
            MessageBox.Show("No Controller is currently pinned (any valid Home RMS Controller can pair).",
                "Home RMS Security", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show($"Remove {count} pinned Controller fingerprint(s)?\nYou will need to re-pair your Controller.",
                "Home RMS Security", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            TrustedControllerStore.ClearAll();
        }
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
