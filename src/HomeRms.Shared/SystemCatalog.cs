using System.Text.Json;

namespace HomeRms.Shared;

/// <summary>One action a controller can request in a system-control area.</summary>
public sealed record SystemAction(
    string Id,
    string Label,
    bool NeedsTarget = false,
    string? ExtraHint = null,
    bool Dangerous = false,
    bool LongRunning = false);

/// <summary>An area of system control (services, updates, ...) with its actions.</summary>
public sealed record SystemArea(
    string Id,
    string Title,
    string TargetHint,
    IReadOnlyList<SystemAction> Actions);

/// <summary>
/// Shared catalog that drives both controller UIs (Windows + macOS) and documents the
/// agent's allowlist. The agent re-validates everything; this is only UI metadata.
/// </summary>
public static class SystemCatalog
{
    public static readonly IReadOnlyList<SystemArea> Areas =
    [
        new("updates", "Windows Update", "KB number (e.g. KB5030211) for 'Install KB'",
        [
            new("status", "Status"),
            new("list-pending", "Check pending"),
            new("install-all", "Install ALL updates", Dangerous: true, LongRunning: true),
            new("install-kb", "Install KB", NeedsTarget: true, Dangerous: true, LongRunning: true),
            new("history", "History"),
            new("pause", "Pause updates", ExtraHint: "days (1-35)"),
            new("resume", "Resume updates"),
            new("disable-auto", "Disable auto-update (policy)", Dangerous: true),
            new("enable-auto", "Enable auto-update"),
            new("restart-service", "Restart update service"),
            new("reset", "Reset components", Dangerous: true),
        ]),
        new("services", "Services", "Service name (e.g. Spooler, wuauserv)",
        [
            new("list", "List all"),
            new("start", "Start", NeedsTarget: true),
            new("stop", "Stop", NeedsTarget: true, Dangerous: true),
            new("restart", "Restart", NeedsTarget: true, Dangerous: true),
            new("set-startup", "Set startup type", NeedsTarget: true,
                ExtraHint: "Automatic | AutomaticDelayed | Manual | Disabled", Dangerous: true),
        ]),
        new("power", "Power", "(not used)",
        [
            new("restart", "Restart", ExtraHint: "delay seconds (default 30)", Dangerous: true),
            new("shutdown", "Shut down", ExtraHint: "delay seconds (default 30)", Dangerous: true),
            new("cancel", "Cancel pending"),
            new("logoff", "Sign out user", Dangerous: true),
            new("lock", "Lock screen"),
            new("sleep", "Sleep", Dangerous: true),
            new("plans", "Power plans"),
            new("set-plan", "Set power plan", NeedsTarget: true),
        ]),
        new("startup", "Startup apps", "Startup entry name (Run key)",
        [
            new("list", "List"),
            new("remove", "Remove", NeedsTarget: true, Dangerous: true),
        ]),
        new("tasks", "Scheduled tasks", "Full task path (e.g. \\Microsoft\\Windows\\Foo\\Bar)",
        [
            new("list", "List"),
            new("run", "Run now", NeedsTarget: true),
            new("enable", "Enable", NeedsTarget: true),
            new("disable", "Disable", NeedsTarget: true, Dangerous: true),
        ]),
        new("registry", "Registry", "Key path (HKLM:\\SOFTWARE\\... or HKCU:\\...)",
        [
            new("list-keys", "List sub-keys", NeedsTarget: true),
            new("get", "Read values", NeedsTarget: true, ExtraHint: "value name (empty = all)"),
            new("set", "Write value", NeedsTarget: true,
                ExtraHint: "name|type|value (type: String, DWord, QWord, ExpandString)", Dangerous: true),
            new("delete", "Delete value", NeedsTarget: true, ExtraHint: "value name", Dangerous: true),
        ]),
        new("firewall", "Firewall", "(not used)",
        [
            new("status", "Profiles status"),
            new("enable-all", "Enable all profiles"),
            new("disable-all", "Disable all profiles", Dangerous: true),
        ]),
        new("system", "System settings", "Value (hostname / GUID / time-zone id)",
        [
            new("info", "System info"),
            new("hotfixes", "Installed hotfixes"),
            new("users", "Local users"),
            new("defender-status", "Defender status"),
            new("defender-update", "Defender: update signatures"),
            new("defender-scan", "Defender: quick scan", LongRunning: true),
            new("rdp-on", "Enable Remote Desktop (RDP)", Dangerous: true),
            new("rdp-off", "Disable Remote Desktop (RDP)"),
            new("rename", "Rename computer", NeedsTarget: true, Dangerous: true),
            new("timezone", "Set time zone", NeedsTarget: true),
        ]),
    ];

    public static SystemArea? Find(string id) => Areas.FirstOrDefault(a => a.Id == id);

    public sealed record Win11SettingItem(
        string Area,
        string Action,
        string Title,
        string Subtitle,
        string ButtonLabel,
        bool NeedsTarget = false,
        string? DefaultTarget = null,
        string? DefaultExtra = null,
        string? ExtraHint = null,
        bool Dangerous = false,
        bool LongRunning = false);

    public sealed record Win11SettingsPage(
        string Id,
        string Icon,
        string Title,
        string Subtitle,
        string TargetHint,
        IReadOnlyList<Win11SettingItem> Items);

    /// <summary>
    /// Windows 11 Settings layout shared identically by both Windows and macOS Controllers.
    /// </summary>
    public static readonly IReadOnlyList<Win11SettingsPage> Win11Pages =
    [
        new("home", "HOME", "Home", "Device overview, quick settings, and remote status", "Optional target",
        [
            new("system", "info", "Device Specifications", "View computer name, Windows edition, build, RAM, and uptime", "View Info"),
            new("updates", "status", "Windows Update Status", "Check update service state, pending reboot, and pause status", "Check Status"),
            new("system", "network-list", "Network & Internet", "Inspect active network adapters, link speed, and MAC addresses", "View Adapters"),
            new("system", "theme-dark", "Personalization — Dark Mode", "Switch Windows apps and taskbar to Dark theme", "Set Dark"),
            new("system", "theme-light", "Personalization — Light Mode", "Switch Windows apps and taskbar to Light theme", "Set Light"),
            new("power", "lock", "Lock Workstation", "Immediately lock the remote Windows session", "Lock Screen"),
        ]),
        new("system", "SYS", "System", "Display, Power & battery, Remote Desktop, About & Rename", "Computer name or Power Plan GUID",
        [
            new("system", "info", "About This PC", "OS version, hardware summary, domain, and last boot time", "Refresh Info"),
            new("system", "hotfixes", "Installed Updates (Hotfixes)", "List all installed Windows KB hotfixes and dates", "List Hotfixes"),
            new("system", "rename", "Rename This PC", "Change the Windows computer name (requires restart)", "Rename PC", NeedsTarget: true, Dangerous: true),
            new("system", "rdp-on", "Remote Desktop (RDP) — Enable", "Allow incoming Windows Remote Desktop connections & firewall rule", "Enable RDP", Dangerous: true),
            new("system", "rdp-off", "Remote Desktop (RDP) — Disable", "Block incoming Windows Remote Desktop connections", "Disable RDP"),
            new("power", "plans", "Power Plans", "List available Windows power plans and active GUID", "List Plans"),
            new("power", "set-plan", "Activate Power Plan", "Set active power plan using its GUID in Target", "Set Plan", NeedsTarget: true),
            new("power", "restart", "Restart Computer", "Schedule a remote restart (Extra = delay seconds, default 30)", "Restart", DefaultExtra: "30", ExtraHint: "delay seconds", Dangerous: true),
            new("power", "shutdown", "Shut Down Computer", "Schedule a remote shutdown (Extra = delay seconds, default 30)", "Shut Down", DefaultExtra: "30", ExtraHint: "delay seconds", Dangerous: true),
            new("power", "cancel", "Cancel Pending Restart/Shutdown", "Abort a scheduled shutdown.exe restart or power off", "Cancel"),
            new("power", "sleep", "Sleep", "Put the remote computer into sleep mode", "Sleep", Dangerous: true),
            new("power", "logoff", "Sign Out Current User", "Sign out the active Windows user session", "Sign Out", Dangerous: true),
        ]),
        new("devices", "DEV", "Bluetooth & devices", "Connected hardware, Bluetooth service, and Printers", "Service name (bthserv / Spooler)",
        [
            new("system", "devices-list", "Connected Devices (PnP)", "List present hardware, Bluetooth, USB, audio, and display devices", "List Devices"),
            new("services", "restart", "Restart Bluetooth Support Service", "Restart Windows Bluetooth service (bthserv)", "Restart Bluetooth", DefaultTarget: "bthserv", Dangerous: true),
            new("services", "restart", "Restart Print Spooler", "Restart Windows printing queue service (Spooler)", "Restart Spooler", DefaultTarget: "Spooler", Dangerous: true),
        ]),
        new("network", "NET", "Network & internet", "Adapters, DNS cache, and Windows Firewall profiles", "Optional",
        [
            new("system", "network-list", "Network Adapters", "Show Ethernet/Wi-Fi adapters, link speeds, and connection status", "List Adapters"),
            new("system", "dns-flush", "Flush DNS Resolver Cache", "Clear the local Windows DNS cache (ipconfig /flushdns)", "Flush DNS"),
            new("firewall", "status", "Windows Firewall Status", "Inspect Domain, Private, and Public firewall profiles", "Check Firewall"),
            new("firewall", "enable-all", "Enable All Firewall Profiles", "Turn on Windows Defender Firewall for all network profiles", "Enable All"),
            new("firewall", "disable-all", "Disable All Firewall Profiles", "Turn off Windows Defender Firewall on all profiles", "Disable All", Dangerous: true),
        ]),
        new("personalization", "UI", "Personalization", "Colors, Dark/Light mode, Taskbar alignment, Explorer", "Optional",
        [
            new("system", "theme-dark", "App & System Mode: Dark", "Apply Windows 11 Dark theme to apps and shell", "Enable Dark"),
            new("system", "theme-light", "App & System Mode: Light", "Apply Windows 11 Light theme to apps and shell", "Enable Light"),
            new("system", "taskbar-center", "Taskbar Alignment: Center", "Center the Windows 11 Start button and taskbar icons", "Center"),
            new("system", "taskbar-left", "Taskbar Alignment: Left", "Align the Windows 11 Start button to the left", "Align Left"),
            new("system", "file-ext-on", "Show File Extensions", "Always show known file extensions in File Explorer", "Show Extensions"),
            new("system", "file-ext-off", "Hide Known File Extensions", "Hide known file extensions in File Explorer", "Hide Extensions"),
        ]),
        new("apps", "SVC", "Apps & Services", "Startup apps, Windows Services, and Scheduled Tasks", "Service / Startup / Task name",
        [
            new("startup", "list", "Startup Apps", "List programs configured to run at Windows sign-in", "List Startup"),
            new("startup", "remove", "Remove Startup App", "Remove an entry by Name from Windows Run keys", "Remove Entry", NeedsTarget: true, Dangerous: true),
            new("services", "list", "Windows Services", "List all Windows services, states, and startup modes", "List Services"),
            new("services", "start", "Start Service", "Start a Windows service by Name", "Start", NeedsTarget: true),
            new("services", "stop", "Stop Service", "Stop a running Windows service by Name", "Stop", NeedsTarget: true, Dangerous: true),
            new("services", "restart", "Restart Service", "Restart a Windows service by Name", "Restart", NeedsTarget: true, Dangerous: true),
            new("services", "set-startup", "Set Service Startup Type", "Extra: Automatic, AutomaticDelayed, Manual, or Disabled", "Apply Startup", NeedsTarget: true, DefaultExtra: "Automatic", ExtraHint: "Automatic | AutomaticDelayed | Manual | Disabled", Dangerous: true),
            new("tasks", "list", "Scheduled Tasks", "List all Windows Task Scheduler tasks and states", "List Tasks"),
            new("tasks", "run", "Run Scheduled Task", "Trigger a scheduled task immediately (full TaskPath+Name)", "Run Task", NeedsTarget: true),
            new("tasks", "enable", "Enable Scheduled Task", "Enable a disabled scheduled task", "Enable", NeedsTarget: true),
            new("tasks", "disable", "Disable Scheduled Task", "Disable a scheduled task", "Disable", NeedsTarget: true, Dangerous: true),
        ]),
        new("accounts", "USR", "Accounts", "Local Windows user accounts and sign-in status", "Optional",
        [
            new("system", "users", "Local User Accounts", "List local Windows accounts, enabled state, and last logon", "List Users"),
        ]),
        new("time", "CLK", "Time & language", "Time zone, clock synchronization, and available zones", "Time Zone ID (e.g. Israel Standard Time)",
        [
            new("system", "timezones", "Available Time Zones", "List all valid Windows Time Zone IDs", "List Time Zones"),
            new("system", "timezone", "Set Time Zone", "Change the Windows time zone using a valid Time Zone ID", "Set Time Zone", NeedsTarget: true),
            new("system", "time-sync", "Sync Clock Now", "Force Windows Time service (w32tm) to synchronize immediately", "Sync Clock"),
        ]),
        new("gaming", "PERF", "Gaming", "Windows Game Mode optimization", "Optional",
        [
            new("system", "gamemode-on", "Game Mode — On", "Optimize Windows for gaming performance", "Turn On"),
            new("system", "gamemode-off", "Game Mode — Off", "Disable Windows Game Mode", "Turn Off"),
        ]),
        new("privacy", "SEC", "Privacy & security", "Windows Defender Antivirus, Firewall, and Registry Editor", "Registry Path (HKLM:\\... or HKCU:\\...)",
        [
            new("system", "defender-status", "Windows Defender Status", "Check real-time protection, signature version, and last scan", "Check Status"),
            new("system", "defender-update", "Update Defender Signatures", "Download latest Microsoft Defender virus definitions", "Update Signatures"),
            new("system", "defender-scan", "Run Defender Quick Scan", "Start a Microsoft Defender Quick Scan on the remote PC", "Quick Scan", LongRunning: true),
            new("registry", "list-keys", "Registry: List Sub-Keys", "Browse sub-keys under HKLM:\\ or HKCU:\\", "List Sub-Keys", NeedsTarget: true, DefaultTarget: @"HKLM:\SOFTWARE"),
            new("registry", "get", "Registry: Read Values", "Read all values (or Extra = specific value name)", "Read Values", NeedsTarget: true, DefaultTarget: @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"),
            new("registry", "set", "Registry: Write Value", "Extra format: name|type|value (String, DWord, QWord, ExpandString)", "Write Value", NeedsTarget: true, ExtraHint: "name|type|value", Dangerous: true),
            new("registry", "delete", "Registry: Delete Value", "Extra = value name to delete from Target key", "Delete Value", NeedsTarget: true, ExtraHint: "value name", Dangerous: true),
            new("agent", "autostart-status", "Agent Auto-Start Status", "Check if Home RMS Agent is registered to start automatically with Windows", "Check Status"),
            new("agent", "autostart-enable", "Enable Agent Auto-Start (Startup / Logon)", "Register elevated Task Scheduler auto-start at logon/startup without UAC prompts", "Enable Auto-Start"),
            new("agent", "autostart-disable", "Disable Agent Auto-Start", "Remove Home RMS Agent auto-start task and registry entry", "Disable Auto-Start", Dangerous: true),
        ]),
        new("updates", "UPD", "Windows Update", "Check, install, pause, and manage Windows Updates", "KB number (e.g. KB5030211)",
        [
            new("updates", "status", "Update Status", "Reboot required, service status, policy, and pause expiry", "Status"),
            new("updates", "list-pending", "Check for Updates", "Search Microsoft Update for pending updates", "Check Pending", LongRunning: true),
            new("updates", "install-all", "Install All Pending Updates", "Download and install all available Windows updates", "Install All", Dangerous: true, LongRunning: true),
            new("updates", "install-kb", "Install Specific KB", "Download and install only the KB entered in Target", "Install KB", NeedsTarget: true, Dangerous: true, LongRunning: true),
            new("updates", "history", "Update History", "View the last 60 Windows Update installation results", "View History"),
            new("updates", "pause", "Pause Updates", "Pause updates for Extra days (1–35)", "Pause", DefaultExtra: "7", ExtraHint: "days (1-35)"),
            new("updates", "resume", "Resume Updates", "Clear update pause and resume normal updates", "Resume"),
            new("updates", "disable-auto", "Disable Automatic Updates (Policy)", "Set NoAutoUpdate=1 policy in HKLM", "Disable Auto", Dangerous: true),
            new("updates", "enable-auto", "Enable Automatic Updates", "Remove NoAutoUpdate policy restriction", "Enable Auto"),
            new("updates", "restart-service", "Restart Update Services", "Restart wuauserv and BITS services", "Restart Services"),
            new("updates", "reset", "Reset Windows Update Components", "Rename SoftwareDistribution to .old and restart services", "Reset Components", Dangerous: true),
        ]),
    ];

    /// <summary>Parsed tabular result: ordered columns + rows.</summary>
    public sealed record Table(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, string>> Rows)
    {
        public static readonly Table Empty = new(Array.Empty<string>(), Array.Empty<Dictionary<string, string>>());

        public IReadOnlyList<int> Widths()
        {
            var widths = new int[Columns.Count];
            for (var i = 0; i < Columns.Count; i++)
            {
                widths[i] = Columns[i].Length;
                foreach (var r in Rows)
                    widths[i] = Math.Max(widths[i], Math.Min(r.GetValueOrDefault(Columns[i], "").Length, 48));
            }
            return widths;
        }

        public string FormatHeader() => Format(i => Columns[i]);

        public string FormatRow(Dictionary<string, string> row) => Format(i => row.GetValueOrDefault(Columns[i], ""));

        private string Format(Func<int, string> cell)
        {
            var w = Widths();
            var parts = new string[Columns.Count];
            for (var i = 0; i < Columns.Count; i++)
            {
                var text = cell(i).Replace('\r', ' ').Replace('\n', ' ');
                if (text.Length > 48) text = text[..47] + "…";
                parts[i] = text.PadRight(w[i]);
            }
            return string.Join("  ", parts);
        }
    }

    /// <summary>Parses the agent's JSON (array or single object) into a <see cref="Table"/>.</summary>
    public static Table ParseTable(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Table.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];

            var columns = new List<string>();
            var rows = new List<Dictionary<string, string>>();
            foreach (var el in elements)
            {
                var row = new Dictionary<string, string>();
                if (el.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in el.EnumerateObject())
                    {
                        if (!columns.Contains(p.Name)) columns.Add(p.Name);
                        row[p.Name] = p.Value.ValueKind == JsonValueKind.String
                            ? p.Value.GetString() ?? ""
                            : p.Value.ValueKind == JsonValueKind.Null ? "" : p.Value.ToString();
                    }
                }
                else
                {
                    if (!columns.Contains("Value")) columns.Add("Value");
                    row["Value"] = el.ToString();
                }
                rows.Add(row);
            }
            return new Table(columns, rows);
        }
        catch (JsonException)
        {
            return Table.Empty;
        }
    }

    /// <summary>Derives the "target" text box value from a selected row of the given area.</summary>
    public static string TargetFromRow(string areaId, Dictionary<string, string> row)
    {
        string Get(string k) => row.GetValueOrDefault(k, "");
        return areaId switch
        {
            "services" or "startup" => Get("Name"),
            "tasks" => Get("TaskPath") + Get("TaskName"),
            "updates" => Get("KB").Split(',')[0].Trim() is { Length: > 0 } kb ? (kb.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? kb : "KB" + kb) : "",
            "registry" => Get("Name"),
            "power" => Get("Guid"),
            _ => "",
        };
    }
}
