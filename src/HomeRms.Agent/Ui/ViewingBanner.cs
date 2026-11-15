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
        BackColor = Color.FromArgb(200, 30, 30);
        Width = 520;
        Height = 40;

        _label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0)
        };

        var stop = new Button
        {
            Text = "Stop",
            Dock = DockStyle.Right,
            Width = 90,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(150, 20, 20)
        };
        stop.FlatAppearance.BorderSize = 0;
        stop.Click += (_, _) => _onStop();

        Controls.Add(_label);
        Controls.Add(stop);
    }

    public void ShowFor(string operatorName)
    {
        _label.Text = $"👁  \"{operatorName}\" is viewing this screen";
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
