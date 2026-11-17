using System.Drawing;
using System.Windows.Forms;

namespace HomeRms.Agent.Ui;

/// <summary>
/// Always-on-top banner shown at the top of the screen whenever a remote operator
/// is viewing. It cannot be dismissed without stopping the session, so the person
/// at the machine is always aware they are being viewed.
/// </summary>
internal sealed class ViewingBanner : Form
{
    private readonly Label _label;
    private readonly Action _onStop;

    public ViewingBanner(Action onStop)
    {
        _onStop = onStop;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(26, 9, 15);
        Width = 560;
        Height = 46;
        Padding = new Padding(1);
        Theme.ApplyWindowChrome(this);

        _label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(36, 0, 0, 0)
        };
        // Red "live" dot drawn left of the text
        _label.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var glow = new SolidBrush(Color.FromArgb(70, Theme.Danger));
            e.Graphics.FillEllipse(glow, 10, _label.Height / 2 - 9, 18, 18);
            using var dot = new SolidBrush(Theme.Danger);
            e.Graphics.FillEllipse(dot, 14, _label.Height / 2 - 5, 10, 10);
        };

        var stop = Theme.Flat("Stop", Theme.Danger, Color.White, (_, _) => _onStop(), 88);
        stop.Dock = DockStyle.Right;
        var stopWrap = new Panel { Dock = DockStyle.Right, Width = 100, Padding = new Padding(0, 6, 8, 6), BackColor = BackColor };
        stopWrap.Controls.Add(stop);

        Controls.Add(_label);
        Controls.Add(stopWrap);
    }

    public void ShowFor(string operatorName)
    {
        _label.Text = $"\"{operatorName}\" is viewing this screen";
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 8);
        Show();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }
}
