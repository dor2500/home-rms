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

    /// <summary>A small filled circle used for the tray icon.</summary>
    public static Icon CreateDot(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 14, 14);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
