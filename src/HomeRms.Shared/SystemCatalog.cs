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
