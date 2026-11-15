using System.Drawing;
using System.Windows.Forms;

namespace HomeRms.Agent.Ui;

/// <summary>Single source of truth for the agent's dark visual theme (pixel-accurate across DPIs).</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(15, 17, 23);
    public static readonly Color Surface = Color.FromArgb(21, 25, 34);
    public static readonly Color Card = Color.FromArgb(28, 33, 46);
    public static readonly Color Field = Color.FromArgb(14, 17, 24);
    public static readonly Color Line = Color.FromArgb(40, 47, 64);
    public static readonly Color Text = Color.FromArgb(243, 244, 246);
    public static readonly Color Muted = Color.FromArgb(156, 163, 175);
    public static readonly Color Accent = Color.FromArgb(59, 130, 246);
    public static readonly Color Danger = Color.FromArgb(239, 68, 68);
    public static readonly Color Ok = Color.FromArgb(34, 197, 94);

    // Use GraphicsUnit.Pixel so layout coordinates and font sizes stay 1:1 on any monitor DPI.
    public static readonly Font UiFont = new("Segoe UI", 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    public static readonly Font CaptionFont = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 22f, FontStyle.Bold, GraphicsUnit.Pixel);
    public static readonly Font ValueFont = new("Segoe UI Semibold", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
    public static readonly Font MonoFont = new("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel);

    public static Button Flat(string text, Color back, Color fore, EventHandler onClick, int width = 0)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = new Font("Segoe UI Semibold", 13.5f, FontStyle.Bold, GraphicsUnit.Pixel),
            Height = 38,
            Cursor = Cursors.Hand,
            Padding = new Padding(14, 0, 14, 0),
            AutoSize = width == 0,
            Width = width == 0 ? 0 : width,
            Margin = new Padding(0, 0, 10, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.1f);
        b.Click += onClick;
        return b;
    }

    /// <summary>Loads the embedded Agent application icon for windows and dialogs.</summary>
    public static Icon LoadAppIcon()
    {
        try
        {
            using var stream = typeof(Theme).Assembly.GetManifestResourceStream("HomeRms.Agent.Assets.agent.ico");
            if (stream is not null)
                return new Icon(stream);
        }
        catch { }

        try
        {
            if (Icon.ExtractAssociatedIcon(Application.ExecutablePath) is { } assoc)
                return assoc;
        }
        catch { }

        return CreateDot(Accent);
    }

    /// <summary>Loads the embedded Agent brand bitmap for headers.</summary>
    public static Bitmap? LoadBrandBitmap()
    {
        try
        {
            using var stream = typeof(Theme).Assembly.GetManifestResourceStream("HomeRms.Agent.Assets.agent.png");
            if (stream is not null)
                return new Bitmap(stream);
        }
        catch { }
        return null;
    }

    /// <summary>Renders a crisp tray icon combining the Agent emblem with a live status indicator dot.</summary>
    public static Icon CreateDot(Color statusColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            using var brand = LoadBrandBitmap();
            if (brand is not null)
            {
                g.DrawImage(brand, new Rectangle(0, 0, 32, 32));
            }
            else
            {
                using var fallbackBrush = new SolidBrush(Surface);
                g.FillEllipse(fallbackBrush, 2, 2, 28, 28);
            }

            // Status indicator dot in bottom-right corner
            using var ringBrush = new SolidBrush(Bg);
            g.FillEllipse(ringBrush, 19, 19, 13, 13);
            using var dotBrush = new SolidBrush(statusColor);
            g.FillEllipse(dotBrush, 21, 21, 9, 9);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
