using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using HomeRms.Agent.Core;
using HomeRms.Shared.Updates;

namespace HomeRms.Agent.Ui;

/// <summary>
/// The agent's main window: current status, the active remote session (with a
/// one-click stop), agent updates, and the live activity log. Closing it hides to the tray; the
/// agent keeps running until "Exit agent" is chosen.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ActivityLog _log;
    private readonly Label _statusValue;
    private readonly Label _sessionTitle;
    private readonly Label _sessionDetail;
    private readonly Button _stopButton;
    private readonly TextBox _logBox;
    private bool _exiting;

    public event Action? RevokeRequested;
    public event Action? ExitRequested;
    public event Action<bool>? AutoApproveChanged;

    private readonly CheckBox _autoApproveCheck;

    // ---------- updates card ----------
    private const int UpdatesCollapsedHeight = 136;
    private const int UpdatesExpandedHeight = 288;
    private readonly AgentUpdater _updater;
    private readonly RoundPanel _updCard;
    private readonly Label _updStatus;
    private readonly Label _updMeta;
    private readonly Label _updDetail;
    private readonly Label _updLatest;
    private readonly TextBox _updNotes;
    private readonly RoundPanel _updNotesCard;
    private readonly Button _updCheckBtn;
    private readonly Button _updNowBtn;
    private readonly ThinProgressBar _updProgress;
    private readonly Label _updProgressText;
    private readonly CheckBox _updAutoCheck;
    private readonly Button[] _updInterval;
    private readonly Label _updIntervalLabel;
    private readonly ToolTip _tips = new();
    private bool _updBusy;

    public MainForm(ActivityLog log, int port, AgentUpdater updater, bool initialAutoApprove = false)
    {
        _log = log;
        _updater = updater;

        var ver = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var verStr = $"v{ver.Major}.{ver.Minor}.{ver.Build}";

        AutoScaleMode = AutoScaleMode.None;
        Text = $"Home RMS Agent {verStr} — Endpoint Monitor";
        Icon = Theme.LoadAppIcon();
        ClientSize = new Size(880, 800);
        MinimumSize = new Size(760, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;
        Theme.ApplyWindowChrome(this);

        // ---------- header ----------
        var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Theme.Bg, Padding = new Padding(28, 0, 28, 0) };
        header.Paint += (_, e) => Theme.PaintBackdrop(e.Graphics, header.ClientRectangle);
        header.Resize += (_, _) => header.Invalidate();
        if (Theme.LoadBrandBitmap() is { } logoBmp)
        {
            var logoBox = new PictureBox
            {
                Image = logoBmp,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(52, 52),
                Location = new Point(28, 22),
                BackColor = Color.Transparent,
            };
            header.Controls.Add(logoBox);
        }
        var appTitle = new Label
        {
            Text = "Home RMS Agent",
            Font = Theme.TitleFont,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(92, 20),
        };
        var appSub = new Label
        {
            Text = "This computer can be viewed or controlled only with your explicit permission. All activity is logged below.",
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Font = Theme.UiFont,
            AutoSize = true,
            Location = new Point(92, 56),
        };
        var verBadge = new Label
        {
            Text = $"{verStr}  •  github.com/dor2500/home-rms",
            ForeColor = Theme.Accent,
            BackColor = Color.Transparent,
            Font = Theme.UiFont,
            AutoSize = true,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(580, 26),
        };
        verBadge.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/dor2500/home-rms") { UseShellExecute = true }); } catch { }
        };
        header.Controls.Add(appTitle);
        header.Controls.Add(appSub);
        header.Controls.Add(verBadge);

        // ---------- body ----------
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 16), BackColor = Theme.Bg };

        // Bottom action bar
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 0) };
        _autoApproveCheck = new CheckBox
        {
            Text = "Auto-approve connections (no prompt)",
            Checked = initialAutoApprove,
            ForeColor = Theme.Text,
            BackColor = Theme.Bg,
            Font = Theme.UiFont,
            AutoSize = true,
            Cursor = Cursors.Hand,
            Location = new Point(0, 16),
        };
        _autoApproveCheck.CheckedChanged += (_, _) => AutoApproveChanged?.Invoke(_autoApproveCheck.Checked);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 430,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Bg,
        };
        var exitBtn = Theme.Flat("Exit Agent", Theme.Danger, Theme.OnAccent, (_, _) => RequestExit(), 130);
        var hideBtn = Theme.Flat("Hide to Tray", Theme.Card, Theme.Text, (_, _) => Hide(), 130);
        var tfaBtn = Theme.Flat("🔐 2FA Setup", Theme.Card, Theme.Accent, (_, _) => TrayApplicationContext.Show2FaDialog(), 135);
        actions.Controls.Add(exitBtn);
        actions.Controls.Add(hideBtn);
        actions.Controls.Add(tfaBtn);
        bottomBar.Controls.Add(_autoApproveCheck);
        bottomBar.Controls.Add(actions);

        // Activity log (fills remaining space)
        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 18, 0, 0) };
        var logCard = new RoundPanel { Dock = DockStyle.Fill, BackColor = Theme.Field, Padding = new Padding(14, 12, 6, 12), Radius = 14 };
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            Font = Theme.MonoFont,
        };
        var logCaption = new Label
        {
            Text = "LIVE ACTIVITY LOG",
            Font = Theme.CaptionFont,
            ForeColor = Theme.Muted,
            Dock = DockStyle.Top,
            Height = 26,
        };
        _logBox.HandleCreated += (_, _) => Theme.UseDarkScrollbars(_logBox);
        logCard.Controls.Add(_logBox);
        logHost.Controls.Add(logCard);
        logHost.Controls.Add(logCaption);

        // Session card
        var sessionCard = new RoundPanel { Dock = DockStyle.Top, Height = 88, BackColor = Theme.Card, Padding = new Padding(22, 0, 20, 0), Radius = 16, GlowBorder = true };
        var sessionText = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
        _sessionTitle = new Label { Font = Theme.ValueFont, AutoSize = true, Location = new Point(0, 16), ForeColor = Theme.Ok };
        _sessionDetail = new Label { Font = Theme.UiFont, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(0, 44) };
        sessionText.Controls.Add(_sessionTitle);
        sessionText.Controls.Add(_sessionDetail);
        _stopButton = Theme.Flat("Stop Session", Theme.Danger, Theme.OnAccent, (_, _) => RevokeRequested?.Invoke(), 148);
        _stopButton.Dock = DockStyle.Right;
        _stopButton.Enabled = false;
        var stopWrap = new Panel { Dock = DockStyle.Right, Width = 152, Padding = new Padding(0, 24, 0, 24), BackColor = Theme.Card };
        stopWrap.Controls.Add(_stopButton);
        sessionCard.Controls.Add(sessionText);
        sessionCard.Controls.Add(stopWrap);

        // Updates card (collapsed: status row; expanded when a release is available)
        var updHost = new Panel { Dock = DockStyle.Top, Height = UpdatesCollapsedHeight + 16, Padding = new Padding(0, 16, 0, 0), BackColor = Theme.Bg };
        _updCard = new RoundPanel { Dock = DockStyle.Fill, BackColor = Theme.Card, Radius = 16 };
        var updCaption = new Label { Text = "AGENT UPDATES", Font = Theme.CaptionFont, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(22, 14), BackColor = Theme.Card };
        _updStatus = new Label { Font = Theme.ValueFont, ForeColor = Theme.Text, AutoSize = true, Location = new Point(22, 34), BackColor = Theme.Card };
        _updMeta = new Label { Font = Theme.UiFont, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(22, 62), BackColor = Theme.Card };
        _updDetail = new Label { Font = Theme.UiFont, ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true, Location = new Point(22, 84), Height = 20, BackColor = Theme.Card };
        _updCheckBtn = Theme.Flat("Check for updates", Theme.Surface, Theme.Text, (_, _) => StartUpdateCheck(), 180);
        _updAutoCheck = new CheckBox
        {
            Text = "Install updates automatically",
            Checked = updater.AutoInstall,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            Font = Theme.UiFont,
            AutoSize = true,
            Cursor = Cursors.Hand,
        };
        _updAutoCheck.CheckedChanged += (_, _) => _updater.SetAutoInstall(_updAutoCheck.Checked);
        _tips.SetToolTip(_updAutoCheck, "On: new releases are downloaded, verified and installed silently in the background.\nOff: you get a notification and install them yourself.");

        // Segmented "24 h | 48 h" picker for the background check interval (themed buttons; a WinForms
        // ComboBox can't be dark-themed reliably)
        _updInterval = AgentUpdateSettings.AllowedIntervalHours
            .Select(h => Theme.Flat($"{h} h", Theme.Surface, Theme.Muted, (_, _) => _updater.SetCheckIntervalHours(h), 54))
            .ToArray();
        foreach (var b in _updInterval)
        {
            b.Height = 28;
            _tips.SetToolTip(b, "How often the agent checks for a new release in the background (installs silently when auto-install is on).");
        }
        _updIntervalLabel = new Label { Text = "Check every", Font = Theme.UiFont, ForeColor = Theme.Muted, AutoSize = true, BackColor = Theme.Card };
        PaintIntervalButtons(updater.CheckIntervalHours);

        _updLatest = new Label { Font = Theme.CaptionFont, ForeColor = Theme.AccentLight, AutoSize = true, Location = new Point(22, 118), BackColor = Theme.Card };
        _updNotes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            Font = Theme.UiFont,
            Dock = DockStyle.Fill,
        };
        _updNotes.HandleCreated += (_, _) => Theme.UseDarkScrollbars(_updNotes);
        _updNotesCard = new RoundPanel { BackColor = Theme.Field, Padding = new Padding(12, 10, 4, 10), Radius = 12, Location = new Point(22, 142), Height = 128 };
        _updNotesCard.Controls.Add(_updNotes);
        _updNowBtn = Theme.Flat("Update now", Theme.Accent, Theme.OnAccent, (_, _) => StartUpdate(), 190);
        _updProgress = new ThinProgressBar { Width = 190, Visible = false };
        _updProgressText = new Label { Font = Theme.UiFont, ForeColor = Theme.Muted, AutoSize = false, Width = 190, Height = 54, BackColor = Theme.Card };

        _updCard.Controls.AddRange([updCaption, _updStatus, _updMeta, _updDetail, _updCheckBtn, _updAutoCheck, _updIntervalLabel, .. _updInterval,
            _updLatest, _updNotesCard, _updNowBtn, _updProgress, _updProgressText]);
        _updCard.Resize += (_, _) => LayoutUpdatesCard();
        updHost.Controls.Add(_updCard);

        // Stat cards
        var stats = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 104, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 16), WrapContents = false };
        _statusValue = AddStat(stats, "Status", "Listening", 190);
        AddStat(stats, "Endpoint Port", port.ToString(), 180);
        AddStat(stats, "Authentication", "Mutual TLS (PKI)", 210);
        AddStat(stats, "Computer Name", Environment.MachineName, 210);

        body.Controls.Add(logHost);
        body.Controls.Add(bottomBar);
        body.Controls.Add(updHost);
        body.Controls.Add(sessionCard);
        body.Controls.Add(stats);

        Controls.Add(body);
        Controls.Add(header);

        foreach (var line in _log.Snapshot()) _logBox.AppendText(line + Environment.NewLine);
        _log.LineAdded += OnLogLine;

        SetSession(null);

        _updater.StatusChanged += OnUpdaterStatus;
        RenderUpdateStatus(_updater.GetStatus());
        // Status events are dropped while the window has never been shown (no handle yet)
        VisibleChanged += (_, _) => { if (Visible) RenderUpdateStatus(_updater.GetStatus()); };
    }

    // ------------------------------------------------------------------ updates
    /// <summary>Starts a GitHub check off the UI thread; the card refreshes from StatusChanged.</summary>
    public void StartUpdateCheck()
    {
        if (_updBusy) return;
        _updBusy = true;
        _updCheckBtn.Enabled = false;
        _ = Task.Run(async () =>
        {
            try { await _updater.CheckAsync(); }
            catch (Exception ex) { _log.Write($"[updater] check failed: {ex.Message}"); }
            finally { OnUi(() => { _updBusy = false; RenderUpdateStatus(_updater.GetStatus()); }); }
        });
    }

    /// <summary>Downloads and installs the available release (asks first; UAC when the swap needs admin).</summary>
    public void StartUpdate()
    {
        var s = _updater.GetStatus();
        if (_updBusy || s.LastResult is not { UpdateAvailable: true } r) return;
        if (s.SelfUpdateBlockedReason is { } blocked)
        {
            MessageBox.Show(this, blocked, "Home RMS Agent — Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var needsAdmin = _updater.NeedsElevationForSwap(out var adminReason);
        var prompt = $"Install Home RMS Agent {r.LatestTag} now?\n\nThe agent restarts after the update; an active remote session will be disconnected." +
                     (needsAdmin ? $"\n\n{adminReason}\nWindows will ask for administrator approval for the install step." : "");
        if (MessageBox.Show(this, prompt, "Home RMS Agent — Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _updBusy = true;
        RenderUpdateStatus(_updater.GetStatus());
        _ = Task.Run(async () =>
        {
            // Percent is rendered from StatusChanged, so no IProgress sink is needed here.
            try { await _updater.DownloadAndApplyAsync(null, allowElevationPrompt: needsAdmin); }
            catch (Exception ex) { _log.Write($"[updater] update not installed: {ex.Message}"); }
            finally { OnUi(() => { _updBusy = false; RenderUpdateStatus(_updater.GetStatus()); }); }
        });
    }

    private void OnUpdaterStatus(AgentUpdateStatus status) => OnUi(() => RenderUpdateStatus(status));

    private void OnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return; // the constructor renders the initial state itself
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private void RenderUpdateStatus(AgentUpdateStatus s)
    {
        if (IsDisposed) return;
        var r = s.LastResult;
        var latest = r?.LatestTag ?? "";
        (_updStatus.Text, _updStatus.ForeColor) = s.State switch
        {
            AgentUpdateState.Checking => ("Checking for updates…", Theme.Text),
            AgentUpdateState.UpToDate => ("You're up to date", Theme.Ok),
            AgentUpdateState.Available => ($"Update available: {latest}", Theme.AccentLight),
            AgentUpdateState.Downloading => ($"Downloading {latest}… {s.ProgressPercent}%", Theme.AccentLight),
            AgentUpdateState.Applying => ($"Installing {latest} and restarting…", Theme.AccentLight),
            AgentUpdateState.Error => (r is { UpdateAvailable: true } ? "Update failed" : "Update check failed", Theme.Danger),
            _ => ("Not checked yet", Theme.Text),
        };

        var lastChecked = s.LastChecked is { } lc
            ? (lc.Date == DateTimeOffset.Now.Date ? "today " + lc.ToString("HH:mm", CultureInfo.InvariantCulture)
                                                : lc.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture))
            : "never";
        _updMeta.Text = $"Installed {ReleaseVersion.Display(s.Current)}   •   Last checked {lastChecked}";
        _tips.SetToolTip(_updMeta, s.AutoInstall
            ? $"Checks every {s.CheckIntervalHours} hours and installs new releases silently."
            : $"Checks every {s.CheckIntervalHours} hours and only notifies.");
        PaintIntervalButtons(s.CheckIntervalHours);

        string detail;
        var detailColor = Theme.Muted;
        if (s.State == AgentUpdateState.Error) { detail = s.Message; detailColor = Theme.Danger; }
        else if (r is { UpdateAvailable: true } && s.SelfUpdateBlockedReason is { } why) detail = why;
        else if (r is { UpdateAvailable: true } && s.AutoInstall) detail = "It will be installed automatically shortly, or click Update now.";
        else detail = "";
        _updDetail.Text = detail;
        _updDetail.ForeColor = detailColor;
        _tips.SetToolTip(_updDetail, detail);

        var working = s.State is AgentUpdateState.Downloading or AgentUpdateState.Applying;
        var expanded = r is { UpdateAvailable: true } || working;
        if (expanded && r is not null)
        {
            var parts = new List<string> { $"LATEST RELEASE  {latest}" };
            if (r.PublishedAt is { } pub) parts.Add("PUBLISHED " + pub.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant());
            if (r.AssetSize > 0) parts.Add((r.AssetSize / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB");
            _updLatest.Text = string.Join("   •   ", parts);
            var notes = string.IsNullOrWhiteSpace(r.ReleaseNotes) ? "(This release has no release notes.)" : r.ReleaseNotes;
            if (_updNotes.Text != notes) _updNotes.Text = notes;
        }

        _updProgress.Value = s.State == AgentUpdateState.Applying ? 100 : s.ProgressPercent;
        _updProgress.Visible = working;
        _updProgressText.Text = working ? s.Message
            : s.SelfUpdateBlockedReason is not null ? "Install is unavailable for this build (see above)."
            : "Downloads, verifies the SHA-256 checksum and restarts the agent.";
        _updNowBtn.Enabled = !working && !_updBusy && s.CanSelfUpdate && r is { UpdateAvailable: true };
        _updCheckBtn.Enabled = !working && !_updBusy && s.State != AgentUpdateState.Checking;
        if (_updAutoCheck.Checked != s.AutoInstall) _updAutoCheck.Checked = s.AutoInstall;

        foreach (var c in new Control[] { _updLatest, _updNotesCard, _updNowBtn, _updProgressText })
            c.Visible = expanded;

        var height = (expanded ? UpdatesExpandedHeight : UpdatesCollapsedHeight) + 16;
        if (_updCard.Parent is { } host && host.Height != height) host.Height = height;
        LayoutUpdatesCard();
    }

    private void LayoutUpdatesCard()
    {
        var w = _updCard.ClientSize.Width;
        const int right = 22, colW = 190;
        _updCheckBtn.Location = new Point(w - right - _updCheckBtn.Width, 18);
        _updAutoCheck.Location = new Point(w - right - _updAutoCheck.PreferredSize.Width, 70);
        // Row under "Install updates automatically": "Check every [24 h][48 h]", right-aligned
        var x = w - right;
        for (var i = _updInterval.Length - 1; i >= 0; i--)
        {
            x -= _updInterval[i].Width;
            _updInterval[i].Location = new Point(x, 96);
            x -= 6;
        }
        _updIntervalLabel.Location = new Point(x - 4 - _updIntervalLabel.PreferredWidth, 100);
        _updDetail.Width = Math.Max(120, Math.Min(_updAutoCheck.Left, _updIntervalLabel.Left) - 16 - _updDetail.Left);
        var colX = w - right - colW;
        _updNowBtn.Location = new Point(colX, 142);
        _updProgress.Location = new Point(colX, 194);
        _updProgressText.Location = new Point(colX, 212);
        _updNotesCard.Width = Math.Max(160, colX - 20 - _updNotesCard.Left);
    }

    private void PaintIntervalButtons(int selectedHours)
    {
        for (var i = 0; i < _updInterval.Length; i++)
        {
            var on = AgentUpdateSettings.AllowedIntervalHours[i] == selectedHours;
            if (_updInterval[i] is not PillButton pill) continue;
            pill.Fill = on ? Theme.Accent : Theme.Surface;
            pill.Gradient = on;
            pill.ForeColor = on ? Theme.OnAccent : Theme.Muted;
            pill.Invalidate();
        }
    }

    /// <summary>Closes the window for real (used by the UI preview / screenshot mode).</summary>
    public void ForceClose()
    {
        _exiting = true;
        Close();
    }

    public void SetAutoApproveChecked(bool enabled)
    {
        if (_autoApproveCheck.Checked != enabled)
            _autoApproveCheck.Checked = enabled;
    }

    /// <summary>Updates the session card. Pass null when nobody is viewing.</summary>
    public void SetSession(string? operatorName)
    {
        var active = !string.IsNullOrEmpty(operatorName);
        if (active)
        {
            _sessionTitle.Text = $"ACTIVE SESSION: '{operatorName}' is viewing this screen";
            _sessionTitle.ForeColor = Theme.Danger;
            _sessionDetail.Text = "A persistent red banner is displayed on your desktop. Click 'Stop Session' to revoke immediately.";
        }
        else
        {
            _sessionTitle.Text = "No active remote desktop session";
            _sessionTitle.ForeColor = Theme.Ok;
            _sessionDetail.Text = "Your screen is private. Remote viewing requires your explicit consent.";
        }
        _stopButton.Enabled = active;
        _statusValue.Text = active ? "In Session" : "Listening";
        _statusValue.ForeColor = active ? Theme.Danger : Theme.Ok;
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(new Action<string>(AppendLine), line);
        else AppendLine(line);
    }

    private void AppendLine(string line)
    {
        if (_logBox.IsDisposed) return;
        _logBox.AppendText(line + Environment.NewLine);
    }

    private void RequestExit()
    {
        _exiting = true;
        ExitRequested?.Invoke();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _log.LineAdded -= OnLogLine;
            _updater.StatusChanged -= OnUpdaterStatus;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Renders the agent window into a PNG file for documentation.</summary>
    public void SaveScreenshot(string filePath)
    {
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static Label AddStat(Control host, string caption, string value, int width)
    {
        var card = new RoundPanel { Width = width, Height = 84, Margin = new Padding(0, 0, 12, 0), BackColor = Theme.Card, Radius = 14 };
        var cap = new Label
        {
            Text = caption.ToUpperInvariant(),
            Font = Theme.CaptionFont,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(16, 14),
        };
        var val = new Label
        {
            Text = value,
            Font = Theme.ValueFont,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(16, 38),
        };
        card.Controls.Add(cap);
        card.Controls.Add(val);
        host.Controls.Add(card);
        return val;
    }
}
