using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HomeRms.Controller.Services;

/// <summary>
/// Runtime English / Hebrew (עברית) UI localization with RTL FlowDirection support.
/// Translates static labels, buttons, tabs, headers, and tooltips across the WPF tree
/// while keeping code/terminal/remote-screen areas LeftToRight.
/// </summary>
public static class LocalizationService
{
    public const string English = "en";
    public const string Hebrew = "he";

    public static string CurrentLanguage { get; private set; } = English;
    public static bool IsHebrew => string.Equals(CurrentLanguage, Hebrew, StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> EnToHe = new(StringComparer.Ordinal)
    {
        // Top bar & navigation
        ["Overview"] = "סקירה כללית",
        ["Endpoints"] = "מחשבים ונקודות קצה",
        ["Inventory & procs"] = "מלאי ותהליכים",
        ["Remote desktop"] = "שולחן עבודה מרוחק",
        ["PowerShell"] = "טרמינל / PowerShell",
        ["Files"] = "מנהל קבצים",
        ["Services & tasks"] = "שירותים ומשימות",
        ["Hosts & blocker"] = "קובץ Hosts וחוסם אתרים",
        ["System & settings"] = "מערכת והגדרות",
        ["Deploy"] = "התקנת תוכנות",
        ["Windows Update"] = "עדכוני Windows",
        ["Script library"] = "ספריית סקריפטים",
        ["Server & PKI"] = "שרת ואבטחת PKI",
        ["Monitor"] = "ניטור",
        ["Manage"] = "ניהול",
        ["Automate"] = "אוטומציה",
        ["Admin"] = "מנהל מערכת",
        ["Alerts"] = "התראות",
        ["Scan LAN"] = "סרוק רשת",
        ["Console"] = "קונסולה",
        ["Updates"] = "עדכונים",
        ["Add endpoint"] = "הוסף מחשב",
        ["Hub relay online"] = "שרת Hub פעיל",
        ["Standby"] = "המתנה",

        // Overview & Fleet
        ["View all endpoints"] = "הצג את כל המחשבים",
        ["Export fleet report"] = "ייצוא דוח רשת",
        ["Endpoints online"] = "מחשבים מחוברים",
        ["Active tunnel"] = "חיבור פעיל",
        ["Fleet CPU / RAM"] = "עומס CPU / RAM ברשת",
        ["Relay latency (RTT)"] = "זמן תגובה (RTT)",
        ["Relay latency & utilization"] = "זמן תגובה וניצולת",
        ["Audit & relay events"] = "אירועי מערכת וביקורת",
        ["Managed endpoints"] = "מחשבים מנוהלים",
        ["Update all agents"] = "עדכן את כל הסוכנים",
        ["Wake all offline"] = "הער מכובים (WOL)",
        ["Run on all online"] = "הרץ על כל המחוברים",
        ["Broadcast PowerShell"] = "פקודת רוחב (PowerShell)",
        ["Connect"] = "התחבר",
        ["Disconnect"] = "נתק",

        // Remote desktop
        ["Start viewing"] = "התחל צפייה",
        ["Stop viewing"] = "עצור צפייה",
        ["Input control"] = "שליטה בעכבר ומקלדת",
        ["Curtain mode"] = "מסך וילון (פרטיות)",
        ["Send clipboard"] = "שלח לוח",
        ["Pull clipboard"] = "משוך לוח",
        ["Screenshot"] = "צילום מסך",
        ["Full screen (F11)"] = "מסך מלא (F11)",

        // Alert center
        ["Health monitoring"] = "ניטור תקינות",
        ["Alert center"] = "מרכז התראות",
        ["Ack all"] = "אשר הכל",
        ["Clear"] = "נקה",
        ["Alert thresholds"] = "ספי התראה",
        ["Alert types to receive"] = "סוגי התראות לקבלה",
        ["Notifications & background LAN discovery"] = "נוטיפיקציות וסריקת רשת ברקע",
        ["Save alert settings"] = "שמור הגדרות התראות",
        ["Blocked sites"] = "אתרים חסומים",
        ["Offline"] = "ניתוק מחשב",
        ["High CPU"] = "CPU גבוה",
        ["High RAM"] = "זיכרון גבוה",
        ["Low disk"] = "מקום דיסק נמוך",
        ["Thermal"] = "חום גבוה",
        ["Agent warnings"] = "אזהרות סוכן",
        ["Console updates"] = "עדכוני קונסולה",
        ["Desktop toast popup"] = "הודעת Toast על המסך",
        ["Play alert sound"] = "השמע צליל התראה",
        ["Auto-scan LAN every"] = "סרוק רשת אוטומטית כל",
        ["Auto-connect"] = "התחבר אוטומטית",

        // Hosts & website blocker
        ["Hosts & website blocker"] = "עורך Hosts וחוסם אתרים",
        ["Endpoint"] = "מחשב יעד",
        ["Load"] = "טען",
        ["Enable Blocked-Access Listener & Alerts"] = "הפעל מאזין והתראות גישה לאתר חסום",
        ["Apply listener"] = "החל מאזין",
        ["Quick block list"] = "רשימת חסימה מהירה",
        ["Raw hosts editor (advanced)"] = "עריכה ידנית של קובץ Hosts (מתקדם)",
        ["Add domain"] = "הוסף דומיין",
        ["Remove selected"] = "הסר מסומנים",
        ["Dry run"] = "בדיקת שינויים",
        ["Save blocklist"] = "שמור רשימת חסימה",
        ["Automatically include www. subdomains"] = "כלול אוטומטית תת-דומיין www.",
        ["Multi-endpoint push"] = "החלה למספר מחשבים",
        ["Also apply listener setting above"] = "החל גם את הגדרת המאזין למעלה",
        ["Push to checked endpoints"] = "שלח למחשבים המסומנים",
        ["Validate syntax"] = "בדוק תחביר",
        ["Reload from agent"] = "טען מחדש מהסוכן",
        ["Save raw hosts file"] = "שמור קובץ Hosts",
        ["Export"] = "ייצוא",
        ["Import"] = "ייבוא",

        // Services & Files & Scripts
        ["Services & scheduled tasks"] = "שירותים ומשימות מתוזמנות",
        ["Services"] = "שירותים",
        ["Scheduled tasks"] = "משימות מתוזמנות",
        ["Start"] = "הפעל",
        ["Stop"] = "עצור",
        ["Restart"] = "הפעל מחדש",
        ["Refresh"] = "רענן",
        ["Details"] = "פרטים",
        ["Cancel"] = "ביטול",
        ["Close"] = "סגור",
        ["Save"] = "שמור",
        ["Delete"] = "מחק",
        ["Upload"] = "העלה",
        ["Download"] = "הורד",
    };

    private static readonly Dictionary<string, string> HeToEn = BuildReverse();

    private static Dictionary<string, string> BuildReverse()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (en, he) in EnToHe)
            d.TryAdd(he, en);
        return d;
    }

    public static string Normalize(string? lang) =>
        string.Equals(lang, Hebrew, StringComparison.OrdinalIgnoreCase) ? Hebrew : English;

    public static void Apply(Window window, string? lang)
    {
        CurrentLanguage = Normalize(lang);
        var toHe = IsHebrew;
        window.FlowDirection = toHe ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        TranslateSubtree(window, toHe ? EnToHe : HeToEn);
    }

    private static void TranslateSubtree(DependencyObject node, Dictionary<string, string> map)
    {
        switch (node)
        {
            case TextBlock tb when !string.IsNullOrEmpty(tb.Text) && map.TryGetValue(tb.Text, out var t1):
                tb.Text = t1;
                break;
            case HeaderedContentControl hcc:
                if (hcc.Header is string hs && map.TryGetValue(hs, out var h1))
                    hcc.Header = h1;
                if (hcc.Content is string cs && map.TryGetValue(cs, out var c1))
                    hcc.Content = c1;
                break;
            case ContentControl cc when cc.Content is string s && map.TryGetValue(s, out var c2):
                cc.Content = c2;
                break;
        }

        if (node is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip && map.TryGetValue(tip, out var tTip))
                fe.ToolTip = tTip;
            if (fe.Tag is string tag && map.TryGetValue(tag, out var tTag))
                fe.Tag = tTag;
        }

        // Keep technical/code/screen surfaces LTR even in Hebrew RTL mode.
        if (node is FrameworkElement elem && elem.Name is "ScreenImage" or "ShellOutput" or "RawHostsBox" or "ConsoleList")
            elem.FlowDirection = FlowDirection.LeftToRight;

        // Walk logical children (covers TabItems, MenuItems, ContentControls, collapsed panels)
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dep)
                TranslateSubtree(dep, map);
        }

        // Also walk visual children when available (covers templates & DataGrid headers)
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                TranslateSubtree(VisualTreeHelper.GetChild(node, i), map);
        }
    }
}
