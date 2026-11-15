using System.Drawing;
using System.Windows.Forms;
using HomeRms.Agent.Core;

namespace HomeRms.Agent.Ui;

/// <summary>Live view of the activity log so the user can see all remote activity.</summary>
internal sealed class LogWindow : Form
{
    private readonly TextBox _box;
    private readonly ActivityLog _log;

    public LogWindow(ActivityLog log)
    {
        _log = log;
        Text = "Home RMS — activity log";
        ClientSize = new Size(720, 420);
        StartPosition = FormStartPosition.CenterScreen;

        _box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Cascadia Mono, Consolas", 9f),
            BackColor = Color.FromArgb(24, 24, 28),
            ForeColor = Color.Gainsboro
        };
        Controls.Add(_box);

        _box.Lines = _log.Snapshot().ToArray();
        ScrollToEnd();
        _log.LineAdded += OnLineAdded;
    }

    private void OnLineAdded(string line)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _box.AppendText(line + Environment.NewLine);
            });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void ScrollToEnd()
    {
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Keep the agent running; just hide the window.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _log.LineAdded -= OnLineAdded;
            base.OnFormClosing(e);
        }
    }
}
