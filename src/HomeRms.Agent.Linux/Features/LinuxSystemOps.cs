using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeRms.Shared.Protocol;

namespace HomeRms.Agent.Linux.Features;

/// <summary>
/// Allowlisted system-control operations on Linux: systemd services, package updates (apt, dnf, yum,
/// zypper, pacman), power, firewall (ufw / firewalld), systemd timers + crontabs and system info.
/// <para>
/// Security: every user value is validated with a strict regex and handed to bash through the
/// HRMS_TARGET / HRMS_EXTRA environment variables (always double-quoted and preceded by <c>--</c>
/// where the tool supports it), never concatenated into the script text. Tabular output is produced
/// as plain tab-separated text by the scripts and converted to JSON here, so data never has to be
/// JSON-escaped in bash.
/// </para>
/// <para>
/// Column names follow the Windows agent so the Controller views work unchanged: services use
/// Name / DisplayName / State / StartMode, scheduled tasks use TaskPath / TaskName / State
/// (target = TaskPath + TaskName), tables of settings use Setting / Value.
/// </para>
/// </summary>
public static class LinuxSystemOps
{
    public const string NotSupportedPrefix = "Not supported on Linux: ";

    private sealed record Op(Func<Ctx, string?> Script, TimeSpan Timeout, Func<string, string>? Parse = null, int[]? OkExitCodes = null);

    /// <summary>Per-request context: validated target / extra and the detected package manager.</summary>
    private sealed record Ctx(string Target, string Extra, string? PackageManager);

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Update = TimeSpan.FromMinutes(90);

    private const string Marker = "@@HRMS@@";

    // ---------------- Validation (public for the unit checks) ----------------

    /// <summary>systemd unit name; must not start with '-' (no option injection).</summary>
    public static readonly Regex SafeUnit = new(@"^[A-Za-z0-9_][A-Za-z0-9_.\-@:\\]{0,199}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A systemd timer target as listed by tasks/list: "systemd:" + unit ending in ".timer".</summary>
    public static readonly Regex SafeTimerTarget = new(@"^systemd:[A-Za-z0-9_][A-Za-z0-9_.\-@:\\]{0,190}\.timer$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly Regex SafeHost = new(@"^[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static readonly Regex SafeTz = new(@"^[A-Za-z][A-Za-z0-9_+\-]*(?:/[A-Za-z0-9_+\-]+){0,2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static readonly Regex SafePowerProfile = new(@"^[A-Za-z0-9][A-Za-z0-9_\-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static readonly Regex SafeDelay = new(@"^\d{1,5}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> StartupTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Automatic", "AutomaticDelayed", "Manual", "Disabled" };

    /// <summary>Package managers in detection order (first binary found wins).</summary>
    public static readonly IReadOnlyList<(string Name, string Binary)> PackageManagers =
    [
        ("apt", "apt-get"),
        ("dnf", "dnf"),
        ("yum", "yum"),
        ("zypper", "zypper"),
        ("pacman", "pacman"),
    ];

    /// <summary>Returns the first supported package manager whose binary exists, or null.</summary>
    public static string? DetectPackageManager(Func<string, bool> binaryExists)
    {
        foreach (var (name, binary) in PackageManagers)
            if (binaryExists(binary)) return name;
        return null;
    }

    private static readonly string[] SystemBinDirs = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    public static bool BinaryExists(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(SystemBinDirs);
        return dirs.Any(d => File.Exists(Path.Combine(d, name)));
    }

    /// <summary>
    /// Validates the target / extra of an operation. Returns an error message or null when valid.
    /// Public so the unit checks can exercise it without running anything.
    /// </summary>
    public static string? Validate(string key, string target, string extra)
    {
        switch (key)
        {
            case "services/start" or "services/stop" or "services/restart" or "services/set-startup":
                if (!SafeUnit.IsMatch(target)) return "Invalid systemd service name.";
                if (key == "services/set-startup" && !StartupTypes.Contains(extra))
                    return "Startup type must be Automatic, AutomaticDelayed, Manual or Disabled.";
                return null;
            case "tasks/run" or "tasks/enable" or "tasks/disable":
                if (target.StartsWith("cron:", StringComparison.Ordinal))
                    return NotSupportedPrefix + "cron entries are read-only in Home RMS. Edit the crontab on the device (crontab -e) to change them.";
                return SafeTimerTarget.IsMatch(target) ? null : "Invalid task: expected a systemd timer such as 'systemd:apt-daily.timer'.";
            case "power/restart" or "power/shutdown":
                return extra.Length == 0 || (SafeDelay.IsMatch(extra) && int.Parse(extra) <= 86400) ? null : "Delay must be a number of seconds (0-86400).";
            case "power/set-plan":
                return SafePowerProfile.IsMatch(target) ? null : "Invalid power profile name (e.g. balanced, performance, power-saver).";
            case "system/rename":
                return SafeHost.IsMatch(target) ? null : "Invalid hostname (letters, digits and '-', max 63 characters).";
            case "system/timezone":
                return target.Length <= 64 && SafeTz.IsMatch(target) && !target.Contains("..") ? null : "Invalid time zone (e.g. Asia/Jerusalem).";
            default:
                return null;
        }
    }

    // ---------------- Scripts ----------------

    private static Func<Ctx, string?> S(string script) => _ => script;

    private static readonly Dictionary<string, Op> Ops = new()
    {
        // ---- services (systemd) ----
        ["services/list"] = new(S(
            "systemctl list-units --type=service --all --no-pager --plain --no-legend 2>/dev/null; " +
            $"echo '{Marker}'; " +
            "systemctl list-unit-files --type=service --no-pager --plain --no-legend 2>/dev/null; true"),
            Short, ParseServices),
        ["services/start"] = new(S("systemctl start -- \"$HRMS_TARGET\" && echo \"Service '$HRMS_TARGET' started.\""), Short),
        ["services/stop"] = new(S("systemctl stop -- \"$HRMS_TARGET\" && echo \"Service '$HRMS_TARGET' stopped.\""), Short),
        ["services/restart"] = new(S("systemctl restart -- \"$HRMS_TARGET\" && echo \"Service '$HRMS_TARGET' restarted.\""), Short),
        ["services/set-startup"] = new(S(
            "case \"$HRMS_EXTRA\" in " +
            "Automatic|AutomaticDelayed) systemctl enable -- \"$HRMS_TARGET\" && echo \"Service '$HRMS_TARGET' enabled (starts at boot).\" ;; " +
            "Manual) systemctl disable -- \"$HRMS_TARGET\" && echo \"Service '$HRMS_TARGET' set to manual start (disabled at boot).\" ;; " +
            "Disabled) systemctl disable --now -- \"$HRMS_TARGET\" && echo \"Service '$HRMS_TARGET' disabled and stopped.\" ;; " +
            "*) echo 'Invalid startup type.' >&2; exit 2 ;; esac"), Short),

        // ---- package updates ----
        ["updates/status"] = new(c =>
            "printf 'PackageManager\\t%s\\n' \"$HRMS_PM\"; " +
            "printf 'PackageManagerPath\\t%s\\n' \"$(command -v \"$HRMS_PM_BIN\" 2>/dev/null || echo 'not found')\"; " +
            "printf 'Distribution\\t%s\\n' \"$(. /etc/os-release 2>/dev/null; echo \"${PRETTY_NAME:-unknown}\")\"; " +
            "printf 'Kernel\\t%s\\n' \"$(uname -r)\"; " +
            "if [ -f /var/run/reboot-required ]; then r=Yes; elif command -v needs-restarting >/dev/null 2>&1; then needs-restarting -r >/dev/null 2>&1 && r=No || r=Yes; else r=Unknown; fi; " +
            "printf 'RebootRequired\\t%s\\n' \"$r\"; " +
            "printf 'Elevated\\t%s\\n' \"$([ \"$(id -u)\" = 0 ] && echo Yes || echo No)\"",
            Short, ParseKeyValues),
        ["updates/list-pending"] = new(c => c.PackageManager switch
        {
            "apt" => "apt-get update -qq >/dev/null 2>&1; LC_ALL=C apt list --upgradable 2>/dev/null",
            "dnf" => "dnf -q check-update; rc=$?; [ $rc -eq 100 ] && exit 0; exit $rc",
            "yum" => "yum -q check-update; rc=$?; [ $rc -eq 100 ] && exit 0; exit $rc",
            "zypper" => "zypper --non-interactive --quiet refresh >/dev/null 2>&1; zypper --non-interactive --quiet list-updates",
            "pacman" => "if command -v checkupdates >/dev/null 2>&1; then checkupdates; rc=$?; [ $rc -eq 2 ] && exit 0; exit $rc; else pacman -Qu; rc=$?; [ $rc -eq 1 ] && exit 0; exit $rc; fi",
            _ => null,
        }, Long, ParsePendingAuto),
        ["updates/install-all"] = new(c => c.PackageManager switch
        {
            "apt" => "export DEBIAN_FRONTEND=noninteractive; apt-get update -q && apt-get -y -q -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold upgrade",
            "dnf" => "dnf -y upgrade --refresh",
            "yum" => "yum -y update",
            "zypper" => "zypper --non-interactive refresh && zypper --non-interactive update --auto-agree-with-licenses",
            "pacman" => "pacman -Syu --noconfirm",
            _ => null,
        }, Update),

        // ---- power ----
        ["power/restart"] = new(S(
            "d=\"${HRMS_EXTRA:-0}\"; m=$(( (d + 59) / 60 )); shutdown -r \"+$m\" 'Home RMS restart requested' && echo \"Restart scheduled in $m minute(s).\""), Short),
        ["power/shutdown"] = new(S(
            "d=\"${HRMS_EXTRA:-0}\"; m=$(( (d + 59) / 60 )); shutdown -h \"+$m\" 'Home RMS shutdown requested' && echo \"Shutdown scheduled in $m minute(s).\""), Short),
        ["power/cancel"] = new(S("shutdown -c && echo 'Pending shutdown cancelled.'"), Short),
        ["power/sleep"] = new(S("systemctl suspend && echo 'Suspend requested.'"), Short),
        ["power/lock"] = new(S("loginctl lock-sessions && echo 'All sessions locked.'"), Short),
        ["power/logoff"] = new(S(
            "n=0; for s in $(loginctl list-sessions --no-legend 2>/dev/null | awk '{print $1}'); do " +
            "t=$(loginctl show-session \"$s\" -p Type --value 2>/dev/null); c=$(loginctl show-session \"$s\" -p Class --value 2>/dev/null); " +
            "if [ \"$c\" = user ] && { [ \"$t\" = x11 ] || [ \"$t\" = wayland ] || [ \"$t\" = mir ]; }; then " +
            "u=$(loginctl show-session \"$s\" -p Name --value 2>/dev/null); loginctl terminate-session \"$s\" && n=$((n+1)) && echo \"Signed out $u (session $s).\"; fi; done; " +
            "if [ $n -eq 0 ]; then echo 'No active graphical (X11/Wayland) session found.' >&2; exit 1; fi"), Short),
        ["power/plans"] = new(S(
            "if command -v powerprofilesctl >/dev/null 2>&1; then cur=$(powerprofilesctl get); " +
            "for p in $(powerprofilesctl list | sed -n 's/^[* ]*\\([a-z-]*\\):$/\\1/p'); do printf '%s\\t%s\\t%s\\n' \"$p\" \"$p\" \"$([ \"$p\" = \"$cur\" ] && echo Yes || echo No)\"; done; " +
            "elif command -v tuned-adm >/dev/null 2>&1; then cur=$(tuned-adm active | sed 's/^Current active profile: //'); " +
            "for p in $(tuned-adm list | sed -n 's/^- \\([^ ]*\\).*/\\1/p'); do printf '%s\\t%s\\t%s\\n' \"$p\" \"$p\" \"$([ \"$p\" = \"$cur\" ] && echo Yes || echo No)\"; done; " +
            "else echo 'Neither power-profiles-daemon (powerprofilesctl) nor tuned (tuned-adm) is installed.' >&2; exit 1; fi"),
            Short, o => Tsv(o, "Guid", "Name", "Active")),
        ["power/set-plan"] = new(S(
            "if command -v powerprofilesctl >/dev/null 2>&1; then powerprofilesctl set \"$HRMS_TARGET\"; " +
            "elif command -v tuned-adm >/dev/null 2>&1; then tuned-adm profile \"$HRMS_TARGET\"; " +
            "else echo 'No power profile manager found.' >&2; exit 1; fi && echo \"Power profile '$HRMS_TARGET' activated.\""), Short),

        // ---- firewall (ufw, then firewalld) ----
        ["firewall/status"] = new(S(
            "if command -v ufw >/dev/null 2>&1; then ufw status verbose 2>&1; " +
            "elif command -v firewall-cmd >/dev/null 2>&1; then echo \"firewalld: $(firewall-cmd --state 2>&1)\"; firewall-cmd --list-all 2>&1; " +
            "elif command -v nft >/dev/null 2>&1; then nft list ruleset 2>&1 | head -n 200; else iptables -L -n 2>&1; fi"), Short),
        ["firewall/enable-all"] = new(S(
            "if command -v ufw >/dev/null 2>&1; then ufw --force enable; " +
            "elif command -v firewall-cmd >/dev/null 2>&1; then systemctl enable --now firewalld && echo 'firewalld enabled and started.'; " +
            "else echo 'Neither ufw nor firewalld is installed.' >&2; exit 1; fi"), Short),
        ["firewall/disable-all"] = new(S(
            "if command -v ufw >/dev/null 2>&1; then ufw disable; " +
            "elif command -v firewall-cmd >/dev/null 2>&1; then systemctl disable --now firewalld && echo 'firewalld DISABLED and stopped.'; " +
            "else echo 'Neither ufw nor firewalld is installed.' >&2; exit 1; fi"), Short),

        // ---- scheduled tasks: systemd timers + crontabs (read-only) ----
        ["tasks/list"] = new(S(
            "names=$( { systemctl list-units --type=timer --all --no-pager --plain --no-legend 2>/dev/null | awk '{print $1}'; " +
            "systemctl list-unit-files --type=timer --no-pager --plain --no-legend 2>/dev/null | awk '{print $1}'; } | grep -v '@\\.timer$' | sort -u ); " +
            "[ -n \"$names\" ] && systemctl show -p Id -p ActiveState -p UnitFileState -p Triggers -p NextElapseUSecRealtime -p LastTriggerUSec -p Description -- $names 2>/dev/null; " +
            $"echo '{Marker}'; " +
            $"emit() {{ [ -r \"$2\" ] && {{ printf '{Marker}SRC\\t%s\\t%s\\n' \"$1\" \"$2\"; cat -- \"$2\"; echo; }}; }}; " +
            "emit system /etc/crontab; for f in /etc/cron.d/*; do [ -f \"$f\" ] && emit system \"$f\"; done; " +
            "for f in /var/spool/cron/crontabs/* /var/spool/cron/*; do [ -f \"$f\" ] && emit user \"$f\"; done; true"),
            Short, ParseTasks),
        ["tasks/run"] = new(S(
            "u=\"${HRMS_TARGET#systemd:}\"; svc=$(systemctl show -p Triggers --value -- \"$u\" | awk '{print $1}'); " +
            "[ -n \"$svc\" ] || { echo \"Timer '$u' does not trigger a unit.\" >&2; exit 1; }; " +
            "systemctl start -- \"$svc\" && echo \"Started '$svc' (triggered by $u).\""), Short),
        ["tasks/enable"] = new(S("u=\"${HRMS_TARGET#systemd:}\"; systemctl enable --now -- \"$u\" && echo \"Timer '$u' enabled and started.\""), Short),
        ["tasks/disable"] = new(S("u=\"${HRMS_TARGET#systemd:}\"; systemctl disable --now -- \"$u\" && echo \"Timer '$u' disabled and stopped.\""), Short),

        // ---- system ----
        ["system/info"] = new(S(
            "printf 'Hostname\\t%s\\n' \"$(hostname)\"; " +
            "printf 'Distribution\\t%s\\n' \"$(. /etc/os-release 2>/dev/null; echo \"${PRETTY_NAME:-unknown}\")\"; " +
            "printf 'Kernel\\t%s\\n' \"$(uname -srm)\"; " +
            "printf 'Uptime\\t%s\\n' \"$(uptime -p 2>/dev/null || uptime)\"; " +
            "printf 'CPU\\t%s\\n' \"$(sed -n 's/^model name[[:space:]]*: //p' /proc/cpuinfo | head -n1)\"; " +
            "printf 'Cores\\t%s\\n' \"$(nproc 2>/dev/null)\"; " +
            "printf 'Memory\\t%s\\n' \"$(free -h 2>/dev/null | awk '/^Mem:/ {print $2\" total, \"$7\" available\"}')\"; " +
            "printf 'TimeZone\\t%s\\n' \"$(timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null)\"; " +
            "printf 'AgentUser\\t%s\\n' \"$(whoami)\""),
            Short, ParseKeyValues),
        ["system/users"] = new(S("getent passwd 2>/dev/null || cat /etc/passwd"), Short, ParseUsers),
        ["system/rename"] = new(S("hostnamectl set-hostname -- \"$HRMS_TARGET\" && echo \"Hostname set to '$HRMS_TARGET'.\""), Short),
        ["system/timezone"] = new(S("timedatectl set-timezone -- \"$HRMS_TARGET\" && echo \"Time zone set to '$HRMS_TARGET'.\""), Short),
        ["system/timezones"] = new(S("timedatectl list-timezones 2>/dev/null"), Short, o => Tsv(o, "Id")),
        ["system/time-sync"] = new(S(
            "timedatectl set-ntp true 2>/dev/null; " +
            "if command -v chronyc >/dev/null 2>&1; then chronyc -a makestep; else systemctl restart systemd-timesyncd 2>/dev/null; fi; " +
            "echo \"Clock synchronization requested. $(timedatectl show -p NTPSynchronized 2>/dev/null)\""), Short),
        ["system/network-list"] = new(S(
            "ip -br link 2>/dev/null | awk '{printf \"L\\t%s\\t%s\\t%s\\n\", $1, $2, $3}'; " +
            "ip -br addr 2>/dev/null | awk '{a=\"\"; for (i=3;i<=NF;i++) a=a (a==\"\"?\"\":\" \") $i; printf \"A\\t%s\\t%s\\n\", $1, a}'"),
            Short, ParseNetwork),
        ["system/dns-flush"] = new(S(
            "if command -v resolvectl >/dev/null 2>&1; then resolvectl flush-caches; elif command -v systemd-resolve >/dev/null 2>&1; then systemd-resolve --flush-caches; " +
            "elif command -v nscd >/dev/null 2>&1; then nscd -i hosts; else echo 'No DNS cache service found (systemd-resolved / nscd).' >&2; exit 1; fi && echo 'DNS resolver cache flushed.'"), Short),
        ["system/devices-list"] = new(S(
            "command -v lspci >/dev/null 2>&1 && lspci 2>/dev/null | sed 's/^[^ ]* /PCI\\t/; s/: /\\t/'; " +
            "command -v lsusb >/dev/null 2>&1 && lsusb 2>/dev/null | sed 's/^Bus [0-9]* Device [0-9]*: ID [0-9a-f:]* /USB\\tUSB device\\t/'; true"),
            Short, o => Tsv(o, "Class", "Type", "FriendlyName")),

        // ---- agent ----
        ["agent/autostart-status"] = new(S(
            "for u in homerms-agent.service homerms.service; do if systemctl cat -- \"$u\" >/dev/null 2>&1; then " +
            "printf 'Unit\\t%s\\nEnabled\\t%s\\nActive\\t%s\\n' \"$u\" \"$(systemctl is-enabled -- \"$u\" 2>&1)\" \"$(systemctl is-active -- \"$u\" 2>&1)\"; exit 0; fi; done; " +
            "printf 'Unit\\tnone\\nEnabled\\tNo systemd unit found (homerms-agent.service)\\n'"),
            Short, ParseKeyValues),
    };

    /// <summary>Keys of operations that exist on Linux (for diagnostics / the unit checks).</summary>
    public static IEnumerable<string> SupportedOps => Ops.Keys;

    public static bool IsElevated() => Environment.UserName == "root" || Environment.GetEnvironmentVariable("EUID") == "0";

    /// <summary>A clear message for operations that have no Linux equivalent.</summary>
    public static string NotSupportedMessage(string key) => key switch
    {
        _ when key.StartsWith("agent/update-", StringComparison.Ordinal) || key.StartsWith("agent/auto-update-", StringComparison.Ordinal) =>
            NotSupportedPrefix + "agent self-update. Update the Linux agent manually (replace the binary and restart its service).",
        _ when key.StartsWith("agent/autostart-", StringComparison.Ordinal) =>
            NotSupportedPrefix + $"'{key}'. Use 'systemctl enable --now homerms-agent' on the device.",
        _ when key.StartsWith("registry/", StringComparison.Ordinal) => NotSupportedPrefix + "the Windows registry does not exist on Linux.",
        _ when key.StartsWith("startup/", StringComparison.Ordinal) => NotSupportedPrefix + "Windows Run-key startup apps. Use Services (systemd units) instead.",
        "updates/install-kb" or "updates/history" or "updates/pause" or "updates/resume" or "updates/disable-auto" or "updates/enable-auto"
            or "updates/restart-service" or "updates/reset" =>
            NotSupportedPrefix + $"'{key}' is a Windows Update feature. Use 'Check pending' / 'Install ALL updates' (package manager).",
        _ when key.StartsWith("system/defender-", StringComparison.Ordinal) => NotSupportedPrefix + "Microsoft Defender.",
        _ => NotSupportedPrefix + $"'{key}' has no Linux equivalent.",
    };

    public static async Task<SystemOpResult> RunAsync(SystemOpRequest request, CancellationToken ct)
    {
        var elevated = IsElevated();
        var key = $"{request.Area}/{request.Action}";
        if (!Ops.TryGetValue(key, out var op))
            return new SystemOpResult { Success = false, Message = NotSupportedMessage(key), IsElevated = elevated };

        var t = (request.Target ?? "").Trim();
        var x = (request.Extra ?? "").Trim();
        if (Validate(key, t, x) is { } invalid)
            return new SystemOpResult { Success = false, Message = invalid, IsElevated = elevated };

        string? pm = null;
        if (request.Area == "updates")
        {
            pm = DetectPackageManager(BinaryExists);
            if (pm is null && key != "updates/status")
                return new SystemOpResult
                {
                    Success = false,
                    Message = NotSupportedPrefix + "no supported package manager found (apt, dnf, yum, zypper or pacman).",
                    IsElevated = elevated
                };
        }

        var script = op.Script(new Ctx(t, x, pm));
        if (script is null)
            return new SystemOpResult { Success = false, Message = NotSupportedMessage(key), IsElevated = elevated };

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.Environment["HRMS_TARGET"] = t;
        psi.Environment["HRMS_EXTRA"] = x;
        psi.Environment["HRMS_PM"] = pm ?? "unknown";
        psi.Environment["HRMS_PM_BIN"] = PackageManagers.FirstOrDefault(p => p.Name == pm).Binary ?? "";
        psi.Environment["LC_ALL"] = "C";

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(op.Timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return new SystemOpResult { Success = false, Message = $"Operation '{key}' timed out.", IsElevated = elevated };
        }

        var stdout = (await outTask).Trim();
        var stderr = (await errTask).Trim();
        var ok = proc.ExitCode == 0 || (op.OkExitCodes?.Contains(proc.ExitCode) ?? false);

        var res = new SystemOpResult { Success = ok, IsElevated = elevated };
        if (!ok)
        {
            res.Message = Tail(string.IsNullOrWhiteSpace(stderr) ? (string.IsNullOrWhiteSpace(stdout) ? $"Failed (exit {proc.ExitCode})." : stdout) : stderr);
            if (!elevated && res.Message.Contains("ermission", StringComparison.Ordinal))
                res.Message += " (the agent is not running as root)";
        }
        else if (op.Parse is not null)
        {
            try
            {
                res.JsonData = op.Parse(key == "updates/list-pending" ? pm + "\n" + stdout : stdout);
                res.Message = "OK";
            }
            catch (Exception ex)
            {
                res.Success = false;
                res.Message = $"Could not parse the output of '{key}': {ex.Message}";
            }
        }
        else
        {
            res.Message = Tail(string.IsNullOrWhiteSpace(stdout) ? "Done." : stdout);
        }
        return res;
    }

    /// <summary>Keeps the end of very long outputs (package upgrades) within a sensible message size.</summary>
    private static string Tail(string s, int max = 16_000) => s.Length <= max ? s : "…" + s[^max..];

    // ---------------- Parsers (public for the unit checks) ----------------

    private static string ToJson(IEnumerable<IReadOnlyDictionary<string, string>> rows) => JsonSerializer.Serialize(rows);

    private static IEnumerable<string> Lines(string s) =>
        s.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0);

    /// <summary>Tab-separated lines -> JSON array with the given column names (missing cells = "").</summary>
    public static string Tsv(string output, params string[] columns)
    {
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in Lines(output))
        {
            var cells = line.Split('\t');
            var row = new Dictionary<string, string>();
            for (var i = 0; i < columns.Length; i++)
                row[columns[i]] = i < cells.Length ? (i == columns.Length - 1 ? string.Join(" ", cells[i..]) : cells[i]).Trim() : "";
            rows.Add(row);
        }
        return ToJson(rows);
    }

    /// <summary>"Key\tValue" lines -> [{Setting, Value}] (the Windows agent's key/value table shape).</summary>
    public static string ParseKeyValues(string output) =>
        ToJson(Lines(output).Select(l =>
        {
            var tab = l.IndexOf('\t');
            return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["Setting"] = tab < 0 ? l.Trim() : l[..tab].Trim(),
                ["Value"] = tab < 0 ? "" : l[(tab + 1)..].Trim(),
            };
        }));

    /// <summary>Maps systemd ACTIVE / SUB states to the Windows service state names the Controller counts.</summary>
    public static string MapServiceState(string active, string sub) => (active, sub) switch
    {
        (_, "running") => "Running",
        ("failed", _) => "Failed",
        ("activating", _) or (_, "start" or "start-pre" or "start-post") => "Start Pending",
        ("deactivating", _) => "Stop Pending",
        (_, "reloading") => "Running",
        _ => "Stopped",
    };

    /// <summary>Maps a unit-file state to the Windows StartMode names (Auto / Manual / Disabled).</summary>
    public static string MapStartMode(string unitFileState) => unitFileState switch
    {
        "enabled" or "enabled-runtime" or "generated" or "alias" => "Auto",
        "masked" or "masked-runtime" => "Disabled",
        "" => "",
        _ => "Manual", // disabled, static, indirect, transient: can be started on demand
    };

    /// <summary>
    /// list-units output, a marker line, then list-unit-files output -> Name / DisplayName / State /
    /// StartMode / SubState / UnitFileState rows (units that are not loaded are listed as Stopped).
    /// </summary>
    public static string ParseServices(string output)
    {
        var parts = output.Replace("\r", "").Split(Marker);
        var units = parts[0];
        var files = parts.Length > 1 ? parts[1] : "";

        var fileState = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(files))
        {
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 2 && f[0].EndsWith(".service", StringComparison.Ordinal)) fileState[f[0]] = f[1];
        }

        var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var raw in Lines(units))
        {
            var line = raw.TrimStart('●', '*', ' ');
            var f = line.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4 || !f[0].EndsWith(".service", StringComparison.Ordinal)) continue;
            var ufs = fileState.GetValueOrDefault(f[0], "");
            rows[f[0]] = new Dictionary<string, string>
            {
                ["Name"] = f[0],
                ["DisplayName"] = f.Length > 4 ? f[4].Trim() : "",
                ["State"] = MapServiceState(f[2], f[3]),
                ["StartMode"] = MapStartMode(ufs),
                ["SubState"] = f[3],
                ["UnitFileState"] = ufs,
                ["Load"] = f[1],
            };
        }
        foreach (var (name, ufs) in fileState)
        {
            if (rows.ContainsKey(name) || name.Contains("@.", StringComparison.Ordinal)) continue; // templates need an instance
            rows[name] = new Dictionary<string, string>
            {
                ["Name"] = name, ["DisplayName"] = "", ["State"] = "Stopped", ["StartMode"] = MapStartMode(ufs),
                ["SubState"] = "dead", ["UnitFileState"] = ufs, ["Load"] = "not-loaded",
            };
        }
        return ToJson(rows.Values.OrderBy(r => r["Name"], StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// systemctl show blocks (timers), a marker line, then crontab sources -> one merged table:
    /// Type / TaskPath / TaskName / State / Schedule / NextRun / LastRun / Unit / Command / ReadOnly.
    /// </summary>
    public static string ParseTasks(string output)
    {
        var text = output.Replace("\r", "");
        var idx = text.IndexOf(Marker + "\n", StringComparison.Ordinal);
        var timers = idx >= 0 ? text[..idx] : text;
        var cron = idx >= 0 ? text[(idx + Marker.Length + 1)..] : "";
        var rows = new List<Dictionary<string, string>>();

        foreach (var block in timers.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Lines(block))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) kv[line[..eq]] = line[(eq + 1)..].Trim();
            }
            if (!kv.TryGetValue("Id", out var id) || !id.EndsWith(".timer", StringComparison.Ordinal)) continue;
            var ufs = kv.GetValueOrDefault("UnitFileState", "");
            var active = kv.GetValueOrDefault("ActiveState", "");
            rows.Add(new Dictionary<string, string>
            {
                ["Type"] = "systemd timer",
                ["TaskPath"] = "systemd:",
                ["TaskName"] = id,
                ["State"] = ufs is "disabled" or "masked" ? "Disabled" : active == "active" ? "Ready" : "Inactive",
                ["Schedule"] = kv.GetValueOrDefault("Description", ""),
                ["NextRun"] = kv.GetValueOrDefault("NextElapseUSecRealtime", ""),
                ["LastRun"] = kv.GetValueOrDefault("LastTriggerUSec", ""),
                ["Unit"] = kv.GetValueOrDefault("Triggers", ""),
                ["Command"] = "",
                ["ReadOnly"] = "No",
            });
        }

        string source = "", kind = "";
        var lineNo = 0;
        foreach (var raw in cron.Split('\n'))
        {
            if (raw.StartsWith(Marker + "SRC\t", StringComparison.Ordinal))
            {
                var p = raw.Split('\t');
                kind = p.Length > 1 ? p[1] : "system";
                source = p.Length > 2 ? p[2] : "";
                lineNo = 0;
                continue;
            }
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || source.Length == 0) continue;
            // Environment assignments (SHELL=/bin/sh, MAILTO=...) are not jobs.
            if (Regex.IsMatch(line, @"^[A-Za-z_][A-Za-z0-9_]*\s*=")) continue;

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            int scheduleLen = fields[0].StartsWith('@') ? 1 : 5;
            var hasUserField = kind == "system";
            if (fields.Count < scheduleLen + (hasUserField ? 2 : 1)) continue;
            var schedule = string.Join(" ", fields.Take(scheduleLen));
            var user = hasUserField ? fields[scheduleLen] : Path.GetFileName(source);
            var command = string.Join(" ", fields.Skip(scheduleLen + (hasUserField ? 1 : 0)));
            rows.Add(new Dictionary<string, string>
            {
                ["Type"] = "cron",
                ["TaskPath"] = $"cron:{source}:",
                ["TaskName"] = $"line {lineNo} ({user})",
                ["State"] = "Read-only",
                ["Schedule"] = schedule,
                ["NextRun"] = "",
                ["LastRun"] = "",
                ["Unit"] = "",
                ["Command"] = command.Length > 300 ? command[..300] + "…" : command,
                ["ReadOnly"] = "Yes",
            });
        }
        return ToJson(rows);
    }

    /// <summary>First line = package manager name, rest = its list-pending output.</summary>
    private static string ParsePendingAuto(string output)
    {
        var nl = output.IndexOf('\n');
        var pm = nl < 0 ? output : output[..nl];
        return ParsePending(pm.Trim(), nl < 0 ? "" : output[(nl + 1)..]);
    }

    private static readonly Regex AptLine = new(@"^(?<pkg>[^/\s]+)/(?<repo>\S+)\s+(?<new>\S+)\s+(?<arch>\S+)(?:\s+\[upgradable from: (?<cur>[^\]]+)\])?", RegexOptions.Compiled);
    private static readonly Regex RpmLine = new(@"^(?<pkg>\S+)\.(?<arch>[A-Za-z0-9_]+)\s+(?<new>\S+)\s+(?<repo>\S+)\s*$", RegexOptions.Compiled);
    private static readonly Regex PacmanLine = new(@"^(?<pkg>\S+)\s+(?<cur>\S+)\s+->\s+(?<new>\S+)", RegexOptions.Compiled);

    /// <summary>Pending updates of one package manager -> Package / CurrentVersion / NewVersion / Source / Arch rows.</summary>
    public static string ParsePending(string packageManager, string output)
    {
        var rows = new List<Dictionary<string, string>>();
        void Add(string pkg, string cur, string nw, string src, string arch) => rows.Add(new Dictionary<string, string>
        {
            ["Package"] = pkg, ["CurrentVersion"] = cur, ["NewVersion"] = nw, ["Source"] = src, ["Arch"] = arch,
        });

        foreach (var line in Lines(output))
        {
            switch (packageManager)
            {
                case "apt":
                    if (AptLine.Match(line) is { Success: true } a)
                        Add(a.Groups["pkg"].Value, a.Groups["cur"].Value, a.Groups["new"].Value, a.Groups["repo"].Value, a.Groups["arch"].Value);
                    break;
                case "dnf" or "yum":
                    if (line.StartsWith("Obsoleting", StringComparison.Ordinal) || line.StartsWith("Security:", StringComparison.Ordinal)) goto done;
                    if (RpmLine.Match(line) is { Success: true } r)
                        Add(r.Groups["pkg"].Value, "", r.Groups["new"].Value, r.Groups["repo"].Value, r.Groups["arch"].Value);
                    break;
                case "zypper":
                    var c = line.Split('|').Select(s => s.Trim()).ToArray();
                    // S | Repository | Name | Current Version | Available Version | Arch
                    if (c.Length >= 6 && c[2] != "Name" && !c[0].StartsWith('-'))
                        Add(c[2], c[3], c[4], c[1], c[5]);
                    break;
                case "pacman":
                    if (PacmanLine.Match(line) is { Success: true } p)
                        Add(p.Groups["pkg"].Value, p.Groups["cur"].Value, p.Groups["new"].Value, "", "");
                    break;
            }
        }
        done:
        return ToJson(rows);
    }

    /// <summary>passwd lines -> Name / UID / Home / Shell for regular accounts (UID 1000+) and root.</summary>
    public static string ParseUsers(string output) =>
        ToJson(Lines(output)
            .Select(l => l.Split(':'))
            .Where(f => f.Length >= 7 && int.TryParse(f[2], out var uid) && (uid == 0 || (uid >= 1000 && uid < 65534)))
            .Select(f => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["Name"] = f[0], ["UID"] = f[2], ["Home"] = f[5], ["Shell"] = f[6],
                ["Enabled"] = f[6].EndsWith("nologin", StringComparison.Ordinal) || f[6].EndsWith("/false", StringComparison.Ordinal) ? "No" : "Yes",
            }));

    /// <summary>"L\tname\tstate\tmac" and "A\tname\taddresses" lines -> Name / Status / MacAddress / Addresses rows.</summary>
    public static string ParseNetwork(string output)
    {
        var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string> Row(string name) => rows.TryGetValue(name, out var r) ? r : rows[name] = new Dictionary<string, string>
        {
            ["Name"] = name, ["Status"] = "", ["MacAddress"] = "", ["Addresses"] = "",
        };
        foreach (var line in Lines(output))
        {
            var f = line.Split('\t');
            if (f.Length < 3) continue;
            var name = f[1].Split('@')[0];
            if (f[0] == "L") { var r = Row(name); r["Status"] = f[2]; r["MacAddress"] = f.Length > 3 ? f[3] : ""; }
            else if (f[0] == "A") Row(name)["Addresses"] = f[2];
        }
        return ToJson(rows.Values);
    }
}
