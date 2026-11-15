using System.Drawing;
using System.Windows.Forms;
using HomeRms.Agent.Core;

namespace HomeRms.Agent.Ui;

/// <summary>
/// The agent's main window: current status, the active remote session (with a
/// one-click stop), and the live activity log. Closing it hides to the tray; the
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

    public MainForm(ActivityLog log, int port)
    {
        _log = log;

        AutoScaleMode = AutoScaleMode.None;
        Text = "Home RMS Agent — Endpoint Monitor";
        Icon = Theme.LoadAppIcon();
        ClientSize = new Size(880, 620);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;

        // ---------- header ----------
        var header = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = Theme.Surface, Padding = new Padding(28, 0, 28, 0) };
        if (Theme.LoadBrandBitmap() is { } logoBmp)
        {
            var logoBox = new PictureBox
            {
                Image = logoBmp,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(52, 52),
                Location = new Point(28, 17),
                BackColor = Color.Transparent,
            };
            header.Controls.Add(logoBox);
        }
        var appTitle = new Label
        {
            Text = "Home RMS Agent — Consent-Based Endpoint",
            Font = Theme.TitleFont,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(92, 18),
        };
        var appSub = new Label
        {
            Text = "This computer can be viewed or controlled only with your explicit permission. All activity is logged below.",
            ForeColor = Theme.Muted,
            Font = Theme.UiFont,
            AutoSize = true,
            Location = new Point(92, 52),
        };
        header.Controls.Add(appTitle);
        header.Controls.Add(appSub);

        // ---------- body ----------
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 16), BackColor = Theme.Bg };

        // Bottom action bar
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Bg,
            Padding = new Padding(0, 10, 0, 0),
        };
        var exitBtn = Theme.Flat("Exit Agent", Theme.Danger, Color.White, (_, _) => RequestExit(), 135);
        var hideBtn = Theme.Flat("Hide to Tray", Theme.Card, Theme.Text, (_, _) => Hide(), 135);
        actions.Controls.Add(exitBtn);
        actions.Controls.Add(hideBtn);

        // Activity log (fills remaining space)
        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 16, 0, 0) };
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.MonoFont,
        };
        var logCaption = new Label
        {
            Text = "LIVE AUDIT & ACTIVITY LOG",
            Font = Theme.CaptionFont,
            ForeColor = Theme.Muted,
            Dock = DockStyle.Top,
            Height = 24,
        };
        logHost.Controls.Add(_logBox);
        logHost.Controls.Add(logCaption);

        // Session card
        var sessionCard = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Theme.Card, Padding = new Padding(20, 0, 20, 0) };
        var sessionText = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
        _sessionTitle = new Label { Font = Theme.ValueFont, AutoSize = true, Location = new Point(0, 16), ForeColor = Theme.Ok };
        _sessionDetail = new Label { Font = Theme.UiFont, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(0, 44) };
        sessionText.Controls.Add(_sessionTitle);
        sessionText.Controls.Add(_sessionDetail);
        _stopButton = Theme.Flat("Stop Session", Theme.Danger, Color.White, (_, _) => RevokeRequested?.Invoke(), 148);
        _stopButton.Dock = DockStyle.Right;
        _stopButton.Enabled = false;
        var stopWrap = new Panel { Dock = DockStyle.Right, Width = 152, Padding = new Padding(0, 22, 0, 22), BackColor = Theme.Card };
        stopWrap.Controls.Add(_stopButton);
        sessionCard.Controls.Add(sessionText);
        sessionCard.Controls.Add(stopWrap);

        // Stat cards
        var stats = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 100, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 14), WrapContents = false };
        _statusValue = AddStat(stats, "Status", "Listening", 190);
        AddStat(stats, "Endpoint Port", port.ToString(), 180);
        AddStat(stats, "Authentication", "Mutual TLS (PKI)", 210);
        AddStat(stats, "Computer Name", Environment.MachineName, 210);

        body.Controls.Add(logHost);
        body.Controls.Add(actions);
        body.Controls.Add(sessionCard);
        body.Controls.Add(stats);

        Controls.Add(body);
        Controls.Add(header);

        foreach (var line in _log.Snapshot()) _logBox.AppendText(line + Environment.NewLine);
        _log.LineAdded += OnLogLine;

        SetSession(null);
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
        if (disposing) _log.LineAdded -= OnLogLine;
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
        var card = new Panel { Width = width, Height = 82, Margin = new Padding(0, 0, 12, 0), BackColor = Theme.Card };
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
