using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HomeRms.Agent.Ui;

/// <summary>Single source of truth for the agent's dark visual theme (pixel-accurate across DPIs).</summary>
internal static class Theme
{
    // ===== Premium dark palette (shared with the controllers) =====
    public static readonly Color Bg = Color.FromArgb(7, 8, 13);
    public static readonly Color Surface = Color.FromArgb(12, 14, 21);
    public static readonly Color Card = Color.FromArgb(19, 21, 31);
    public static readonly Color Field = Color.FromArgb(11, 12, 19);
    public static readonly Color Line = Color.FromArgb(36, 39, 54);
    public static readonly Color Text = Color.FromArgb(244, 245, 250);
    public static readonly Color Muted = Color.FromArgb(155, 161, 185);
    public static readonly Color Accent = Color.FromArgb(139, 92, 246);
    public static readonly Color Accent2 = Color.FromArgb(34, 211, 238);
    public static readonly Color AccentLight = Color.FromArgb(183, 156, 255);
    public static readonly Color Danger = Color.FromArgb(244, 63, 94);
    public static readonly Color Ok = Color.FromArgb(52, 211, 153);

    // Use GraphicsUnit.Pixel so layout coordinates and font sizes stay 1:1 on any monitor DPI.
    public static readonly Font UiFont = new("Segoe UI", 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    public static readonly Font CaptionFont = new("Segoe UI Semibold", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    public static readonly Font TitleFont = new("Segoe UI", 23f, FontStyle.Bold, GraphicsUnit.Pixel);
    public static readonly Font ValueFont = new("Segoe UI Semibold", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
    public static readonly Font MonoFont = new("Cascadia Mono", 13f, FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>Rounded button. Accent = violet→cyan gradient; any other colour is drawn flat.</summary>
    public static Button Flat(string text, Color back, Color fore, EventHandler onClick, int width = 0)
    {
        var b = new PillButton
        {
            Text = text,
            Fill = back,
            ForeColor = fore,
            Gradient = back == Accent,
            Font = new Font("Segoe UI Semibold", 13.5f, FontStyle.Bold, GraphicsUnit.Pixel),
            Height = 40,
            Cursor = Cursors.Hand,
            Width = width == 0 ? TextRenderer.MeasureText(text, UiFont).Width + 44 : width,
            Margin = new Padding(0, 0, 10, 0),
        };
        b.Click += onClick;
        return b;
    }

    /// <summary>Dark Windows 11 title bar + rounded corners in the app's colours.</summary>
    public static void ApplyWindowChrome(Form form)
    {
        void Apply()
        {
            try
            {
                int on = 1, caption = ToColorRef(Bg), text = ToColorRef(Text), border = ToColorRef(Line), round = 2;
                DwmSetWindowAttribute(form.Handle, 20, ref on, 4);       // immersive dark mode
                DwmSetWindowAttribute(form.Handle, 35, ref caption, 4);  // caption colour
                DwmSetWindowAttribute(form.Handle, 36, ref text, 4);     // caption text colour
                DwmSetWindowAttribute(form.Handle, 34, ref border, 4);   // border colour
                DwmSetWindowAttribute(form.Handle, 33, ref round, 4);    // rounded corners
            }
            catch { /* older Windows */ }
        }
        if (form.IsHandleCreated) Apply(); else form.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Paints the deep background with soft violet / cyan glows (used by headers and dialogs).</summary>
    public static void PaintBackdrop(Graphics g, Rectangle r)
    {
        using (var bg = new SolidBrush(Bg)) g.FillRectangle(bg, r);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PaintGlow(g, new Rectangle(r.Left - r.Width / 3, r.Top - r.Height, r.Width, r.Height * 2), Color.FromArgb(70, Accent));
        PaintGlow(g, new Rectangle(r.Right - r.Width / 2, r.Top - r.Height / 2, r.Width, r.Height * 2), Color.FromArgb(45, Accent2));
    }

    private static void PaintGlow(Graphics g, Rectangle area, Color center)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(area);
        using var brush = new PathGradientBrush(path) { CenterColor = center, SurroundColors = new[] { Color.FromArgb(0, center) } };
        g.FillEllipse(brush, area);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Gives a control the Windows dark scrollbars.</summary>
    public static void UseDarkScrollbars(Control control)
    {
        try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
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

/// <summary>Rounded, owner-drawn button (gradient for the primary action).</summary>
internal sealed class PillButton : Button
{
    private bool _hover, _down;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Color Fill { get; set; } = Theme.Card;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool Gradient { get; set; }

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // parent background behind the rounded corners
        using (var parentBg = new SolidBrush(Parent?.BackColor ?? Theme.Bg)) g.FillRectangle(parentBg, ClientRectangle);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundRect(r, 10);
        var alpha = !Enabled ? 90 : _down ? 200 : 255;

        if (Gradient)
        {
            var a = _hover ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent;
            var b = _hover ? ControlPaint.Light(Theme.Accent2, 0.15f) : Theme.Accent2;
            using var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(alpha, a), Color.FromArgb(alpha, b), 20f);
            g.FillPath(brush, path);
        }
        else
        {
            var fill = _hover ? ControlPaint.Light(Fill, 0.12f) : Fill;
            using var brush = new SolidBrush(Color.FromArgb(alpha, fill));
            g.FillPath(brush, path);
            using var pen = new Pen(Color.FromArgb(Enabled ? 60 : 25, 255, 255, 255));
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle,
            Enabled ? ForeColor : Color.FromArgb(140, ForeColor),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>Panel with rounded corners and a hairline border (cards).</summary>
internal sealed class RoundPanel : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public int Radius { get; set; } = 14;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Color BorderColor { get; set; } = Theme.Line;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool GlowBorder { get; set; }

    public RoundPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var outer = new SolidBrush(Parent?.BackColor ?? Theme.Bg)) g.FillRectangle(outer, ClientRectangle);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundRect(r, Radius);
        using (var fill = new SolidBrush(BackColor)) g.FillPath(fill, path);
        if (GlowBorder)
        {
            using var lg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(150, Theme.Accent), Color.FromArgb(120, Theme.Accent2), 20f);
            using var pen = new Pen(lg, 1.2f);
            g.DrawPath(pen, path);
        }
        else
        {
            using var pen = new Pen(BorderColor);
            g.DrawPath(pen, path);
        }
    }
}