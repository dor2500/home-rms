using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HomeRms.Controller.Themes;

/// <summary>
/// Switches the application between Dark ("Graphite &amp; Iris") and Light ("Alabaster &amp; Iris")
/// theme dictionaries at runtime and updates the Win32 DWM title-bar attributes.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    private static readonly Uri DarkTokensUri = new("/HomeRmsController;component/Themes/Tokens.xaml", UriKind.Relative);
    private static readonly Uri LightTokensUri = new("/HomeRmsController;component/Themes/Tokens.Light.xaml", UriKind.Relative);

    private static ResourceDictionary? _activeOverride;

    public static string CurrentTheme { get; private set; } = Dark;
    public static bool IsLight => string.Equals(CurrentTheme, Light, StringComparison.OrdinalIgnoreCase);

    public static void Apply(string? theme, Window? window = null)
    {
        var next = string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        CurrentTheme = next;

        var app = Application.Current;
        if (app is not null)
        {
            app.ThemeMode = next == Light ? ThemeMode.Light : ThemeMode.Dark;

            var merged = app.Resources.MergedDictionaries;
            if (_activeOverride is not null)
            {
                merged.Remove(_activeOverride);
                _activeOverride = null;
            }
            var dict = new ResourceDictionary { Source = next == Light ? LightTokensUri : DarkTokensUri };
            merged.Add(dict);
            _activeOverride = dict;
        }

        Palette.Invalidate();

        if (window is not null)
            ApplyWindowChrome(window);
        else if (app?.MainWindow is { } mw)
            ApplyWindowChrome(mw);
    }

    public static void ApplyWindowChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var caption = Palette.ColorRef("SurfaceLayerBrush");
        var text = Palette.ColorRef("TextPrimaryBrush");
        var border = Palette.ColorRef("BorderSubtleBrush");
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));    // DWMWA_TEXT_COLOR
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));  // DWMWA_BORDER_COLOR
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
