using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace HomeRms.Agent.Features;

[SupportedOSPlatform("windows")]
internal static class PrivacyCurtain
{
    [DllImport("user32.dll")]
    private static extern bool BlockInput(bool fBlockIt);

    private static readonly object Gate = new();
    private static readonly List<Form> Curtains = [];
    private static Thread? _uiThread;

    public static bool Enable(string bannerText)
    {
        Disable();
        lock (Gate)
        {
            var ready = new ManualResetEventSlim(false);
            _uiThread = new Thread(() =>
            {
                try { BlockInput(true); } catch { }
                foreach (var screen in Screen.AllScreens)
                {
                    var form = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None,
                        BackColor = Color.Black,
                        TopMost = true,
                        StartPosition = FormStartPosition.Manual,
                        Bounds = screen.Bounds,
                        ShowInTaskbar = false
                    };
                    var label = new Label
                    {
                        Text = string.IsNullOrWhiteSpace(bannerText)
                            ? "System under maintenance — Remote session active"
                            : bannerText,
                        ForeColor = Color.FromArgb(14, 165, 233),
                        Font = new Font("Segoe UI", 24, FontStyle.Bold),
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleCenter
                    };
                    form.Controls.Add(label);
                    form.Show();
                    Curtains.Add(form);
                }
                ready.Set();
                Application.Run();
            });
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.IsBackground = true;
            _uiThread.Start();
            ready.Wait(2000);
        }
        return true;
    }

    public static void Disable()
    {
        lock (Gate)
        {
            foreach (var f in Curtains)
            {
                try
                {
                    if (!f.IsDisposed)
                        f.Invoke(() => { try { BlockInput(false); } catch { } f.Close(); });
                }
                catch { }
            }
            try { BlockInput(false); } catch { }
            Curtains.Clear();
            try { Application.ExitThread(); } catch { }
            _uiThread = null;
        }
    }
}
