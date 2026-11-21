using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace HomeRms.Controller.Themes;

/// <summary>Semantic tone used by code-built pills, badges and status dots.</summary>
public enum PaletteTone { Neutral, Accent, Success, Warning, Danger, Info }

/// <summary>
/// Code-side access to the design tokens in Themes\Tokens.xaml, so colours built in C#
/// (status pills, event badges, endpoint dots, alert severities…) never hard-code hex values.
/// Brushes are resolved from Application resources once and frozen, so the getters are
/// cheap and safe to call from any thread.
/// </summary>
public static class Palette
{
    private static readonly ConcurrentDictionary<string, Brush> Cache = new();

    /// <summary>Raised on the UI thread after the active theme dictionary changes.</summary>
    public static event Action? ThemeChanged;

    /// <summary>Clears cached frozen brushes and re-warms from the active theme dictionary.</summary>
    public static void Invalidate()
    {
        Cache.Clear();
        Warm();
        ThemeChanged?.Invoke();
    }

    /// <summary>Resolves a brush token (e.g. "OkBrush", "DangerSoftBrush") as a frozen brush.</summary>
    public static Brush Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;

        Brush? brush = null;
        var app = Application.Current;
        if (app is not null)
        {
            if (app.Dispatcher.CheckAccess())
                brush = app.TryFindResource(key) as Brush;
            else
                brush = app.Dispatcher.Invoke(() => app.TryFindResource(key) as Brush);
        }

        if (brush is null) return Brushes.Transparent; // not cached: resources may not be loaded yet
        if (!brush.IsFrozen && brush.CanFreeze) brush = (Brush)brush.GetAsFrozen();
        Cache[key] = brush;
        return brush;
    }

    /// <summary>Resolves every semantic token once on the UI thread (call at startup) so later lookups never marshal.</summary>
    public static void Warm()
    {
        foreach (var key in new[]
                 {
                     "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush", "TextOnAccentBrush", "SurfaceLayerBrush", "ElevatedBrush",
                     "BorderSubtleBrush", "BorderStrongBrush", "AccentBrush", "AccentStrongBrush", "AccentSoftBrush", "AccentLineBrush",
                     "OkBrush", "SuccessSoftBrush", "SuccessLineBrush", "WarnBrush", "WarningSoftBrush", "WarningLineBrush",
                     "DangerBrush", "DangerSoftBrush", "DangerLineBrush", "InfoBrush", "InfoSoftBrush", "InfoLineBrush",
                     "NeutralBrush", "NeutralSoftBrush",
                 })
            Get(key);
    }

    /// <summary>The colour of a solid brush token (transparent when the key is missing).</summary>
    public static Color GetColor(string key) => Get(key) is SolidColorBrush s ? s.Color : Colors.Transparent;

    // ---- Text & surfaces ----
    public static Brush TextPrimary => Get("TextPrimaryBrush");
    public static Brush TextSecondary => Get("TextSecondaryBrush");
    public static Brush TextMuted => Get("TextMutedBrush");
    public static Brush TextOnAccent => Get("TextOnAccentBrush");
    public static Brush Surface => Get("SurfaceLayerBrush");
    public static Brush Elevated => Get("ElevatedBrush");
    public static Brush BorderSubtle => Get("BorderSubtleBrush");
    public static Brush BorderStrong => Get("BorderStrongBrush");

    // ---- Accent & semantics (solid / soft tint / line tint) ----
    public static Brush Accent => Get("AccentBrush");
    public static Brush AccentStrong => Get("AccentStrongBrush");
    public static Brush AccentSoft => Get("AccentSoftBrush");
    public static Brush AccentLine => Get("AccentLineBrush");
    public static Brush Success => Get("OkBrush");
    public static Brush SuccessSoft => Get("SuccessSoftBrush");
    public static Brush SuccessLine => Get("SuccessLineBrush");
    public static Brush Warning => Get("WarnBrush");
    public static Brush WarningSoft => Get("WarningSoftBrush");
    public static Brush WarningLine => Get("WarningLineBrush");
    public static Brush Danger => Get("DangerBrush");
    public static Brush DangerSoft => Get("DangerSoftBrush");
    public static Brush DangerLine => Get("DangerLineBrush");
    public static Brush Info => Get("InfoBrush");
    public static Brush InfoSoft => Get("InfoSoftBrush");
    public static Brush InfoLine => Get("InfoLineBrush");
    public static Brush Neutral => Get("NeutralBrush");
    public static Brush NeutralSoft => Get("NeutralSoftBrush");

    /// <summary>Foreground colour for a tone (text, dots, glyphs).</summary>
    public static Brush Fg(PaletteTone tone) => tone switch
    {
        PaletteTone.Accent => Accent,
        PaletteTone.Success => Success,
        PaletteTone.Warning => Warning,
        PaletteTone.Danger => Danger,
        PaletteTone.Info => Info,
        _ => TextSecondary,
    };

    /// <summary>Soft tinted background for a tone (pills, badges).</summary>
    public static Brush Soft(PaletteTone tone) => tone switch
    {
        PaletteTone.Accent => AccentSoft,
        PaletteTone.Success => SuccessSoft,
        PaletteTone.Warning => WarningSoft,
        PaletteTone.Danger => DangerSoft,
        PaletteTone.Info => InfoSoft,
        _ => NeutralSoft,
    };

    /// <summary>Tinted 1px border for a tone.</summary>
    public static Brush Line(PaletteTone tone) => tone switch
    {
        PaletteTone.Accent => AccentLine,
        PaletteTone.Success => SuccessLine,
        PaletteTone.Warning => WarningLine,
        PaletteTone.Danger => DangerLine,
        PaletteTone.Info => InfoLine,
        _ => BorderSubtle,
    };

    /// <summary>COLORREF (0x00BBGGRR) for DWM title-bar attributes.</summary>
    public static int ColorRef(string key)
    {
        var c = GetColor(key);
        return c.R | (c.G << 8) | (c.B << 16);
    }
}
