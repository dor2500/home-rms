using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace HomeRms.Controller.Themes;

/// <summary>
/// Switches the application between System (follows Windows Light/Dark), Dark, Light,
/// Midnight OLED, Nordic Frost, and Emerald Ops theme dictionaries at runtime.
/// </summary>
public static class ThemeManager
{
    public const string System = "System";
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string Midnight = "Midnight";
    public const string Nordic = "Nordic";
    public const string Emerald = "Emerald";

    public static readonly IReadOnlyList<(string Id, string Label)> Presets =
    [
        (System, "System"),
        (Dark, "Dark"),
        (Light, "Light"),
        (Midnight, "Midnight"),
        (Nordic, "Nordic"),
        (Emerald, "Emerald"),
    ];

    private static readonly Uri DarkTokensUri = new("/HomeRmsController;component/Themes/Tokens.xaml", UriKind.Relative);
    private static readonly Uri LightTokensUri = new("/HomeRmsController;component/Themes/Tokens.Light.xaml", UriKind.Relative);
    private static readonly Uri MidnightTokensUri = new("/HomeRmsController;component/Themes/Tokens.Midnight.xaml", UriKind.Relative);
    private static readonly Uri NordicTokensUri = new("/HomeRmsController;component/Themes/Tokens.Nordic.xaml", UriKind.Relative);
    private static readonly Uri EmeraldTokensUri = new("/HomeRmsController;component/Themes/Tokens.Emerald.xaml", UriKind.Relative);

    private static ResourceDictionary? _activeOverride;
    private static bool _listeningSystemTheme;

    public static string CurrentTheme { get; private set; } = Dark;
    public static bool IsLight { get; private set; }

    public static string Normalize(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        "system" => System,
        "light" => Light,
        "midnight" => Midnight,
        "nordic" => Nordic,
        "emerald" => Emerald,
        _ => Dark,
    };

    public static bool IsSystemLightMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v != 0;
        }
        catch { }
        return false;
    }

    public static void Apply(string? theme, Window? window = null)
    {
        var chosen = Normalize(theme);
        CurrentTheme = chosen;
        var effective = chosen == System ? (IsSystemLightMode() ? Light : Dark) : chosen;
        IsLight = effective == Light;

        if (chosen == System && !_listeningSystemTheme)
        {
            _listeningSystemTheme = true;
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        }
        else if (chosen != System && _listeningSystemTheme)
        {
            _listeningSystemTheme = false;
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        }

        var app = Application.Current;
        if (app is not null)
        {
            app.ThemeMode = IsLight ? ThemeMode.Light : ThemeMode.Dark;

            var merged = app.Resources.MergedDictionaries;
            if (_activeOverride is not null)
            {
                merged.Remove(_activeOverride);
                _activeOverride = null;
            }
            var uri = effective switch
            {
                Light => LightTokensUri,
                Midnight => MidnightTokensUri,
                Nordic => NordicTokensUri,
                Emerald => EmeraldTokensUri,
                _ => DarkTokensUri,
            };
            var dict = new ResourceDictionary { Source = uri };
            merged.Add(dict);
            _activeOverride = dict;
        }

        Palette.Invalidate();

        if (window is not null)
            ApplyWindowChrome(window);
        else if (app?.MainWindow is { } mw)
            ApplyWindowChrome(mw);
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (CurrentTheme != System) return;
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(System));
    }

    public static void ApplyWindowChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        var caption = Palette.ColorRef("SurfaceLayerBrush");
        var text = Palette.ColorRef("TextPrimaryBrush");
        var border = Palette.ColorRef("BorderSubtleBrush");
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
