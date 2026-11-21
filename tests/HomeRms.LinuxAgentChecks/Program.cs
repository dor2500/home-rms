using System.Text.Json;
using HomeRms.Agent.Linux.Features;
using HomeRms.Shared;

// Offline checks of LinuxSystemOps (no bash, no systemd: only validation, detection and parsing).
var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
    if (!ok) failures.Add(what);
}

List<Dictionary<string, string>> Rows(string json) => SystemCatalog.ParseTable(json).Rows.ToList();

// ---------------- Validation ----------------
Console.WriteLine("[linux] target validation");
string?[] badUnits = ["", "-H", "--host=evil", "a b", "a;reboot", "$(id)", "`id`", "a|b", "a\nb", "a'b", "a\"b", "../x", new string('a', 201)];
foreach (var u in badUnits)
    Check(LinuxSystemOps.Validate("services/start", u!, "") is not null, $"service name rejected: {Show(u!)}");
foreach (var u in new[] { "ssh.service", "getty@tty1.service", "systemd-fsck@dev-disk-by\\x2duuid-1.service", "nginx" })
    Check(LinuxSystemOps.Validate("services/restart", u, "") is null, $"service name accepted: {u}");

Check(LinuxSystemOps.Validate("services/set-startup", "ssh.service", "Manual") is null, "set-startup accepts Manual");
Check(LinuxSystemOps.Validate("services/set-startup", "ssh.service", "mask; reboot") is not null, "set-startup rejects an unknown startup type");

Check(LinuxSystemOps.Validate("tasks/run", "systemd:apt-daily.timer", "") is null, "systemd timer target accepted");
foreach (var t in new[] { "apt-daily.timer", "systemd:-x.timer", "systemd:apt-daily.service", "systemd:a;b.timer", "systemd:$(id).timer", "\\Microsoft\\Windows\\X" })
    Check(LinuxSystemOps.Validate("tasks/enable", t, "") is not null, $"task target rejected: {Show(t)}");
var cronMsg = LinuxSystemOps.Validate("tasks/disable", "cron:/etc/crontab:line 3 (root)", "");
Check(cronMsg is not null && cronMsg.StartsWith(LinuxSystemOps.NotSupportedPrefix) && cronMsg.Contains("read-only"), "cron entries are reported as read-only");

Check(LinuxSystemOps.Validate("power/restart", "", "") is null && LinuxSystemOps.Validate("power/restart", "", "30") is null, "power delay: empty / seconds accepted");
foreach (var d in new[] { "-1", "1e3", "30; reboot", "999999", "$(id)" })
    Check(LinuxSystemOps.Validate("power/shutdown", "", d) is not null, $"power delay rejected: {Show(d)}");

foreach (var h in new[] { "-host", "host-", "a.b", "a b", "a;b", new string('a', 64) })
    Check(LinuxSystemOps.Validate("system/rename", h, "") is not null, $"hostname rejected: {Show(h)}");
Check(LinuxSystemOps.Validate("system/rename", "nas-01", "") is null, "hostname accepted: nas-01");
foreach (var tz in new[] { "../../etc/passwd", "-x", "Asia/../etc", "a b", "Asia/Jerusalem;id" })
    Check(LinuxSystemOps.Validate("system/timezone", tz, "") is not null, $"time zone rejected: {Show(tz)}");
foreach (var tz in new[] { "Asia/Jerusalem", "UTC", "America/Argentina/Buenos_Aires", "Etc/GMT+2" })
    Check(LinuxSystemOps.Validate("system/timezone", tz, "") is null, $"time zone accepted: {tz}");
Check(LinuxSystemOps.Validate("power/set-plan", "balanced", "") is null && LinuxSystemOps.Validate("power/set-plan", "--x", "") is not null, "power profile validation");

// ---------------- Support matrix ----------------
Console.WriteLine("[linux] supported operations");
foreach (var key in new[] { "power/sleep", "power/lock", "power/logoff", "power/plans", "tasks/list", "tasks/run", "tasks/enable", "tasks/disable",
                            "updates/status", "updates/list-pending", "updates/install-all", "services/list", "system/network-list" })
    Check(LinuxSystemOps.SupportedOps.Contains(key), $"supported: {key}");
foreach (var key in new[] { "agent/update-now", "agent/auto-update-on", "registry/get", "updates/install-kb", "system/defender-scan", "system/hotfixes" })
{
    var msg = LinuxSystemOps.NotSupportedMessage(key);
    Check(!LinuxSystemOps.SupportedOps.Contains(key) && msg.StartsWith(LinuxSystemOps.NotSupportedPrefix), $"not supported with a clear message: {key} -> {msg}");
}

// ---------------- Package manager detection ----------------
Console.WriteLine("[linux] package manager detection");
Check(LinuxSystemOps.DetectPackageManager(b => b == "apt-get") == "apt", "apt-get -> apt");
Check(LinuxSystemOps.DetectPackageManager(b => b is "dnf" or "yum") == "dnf", "dnf preferred over yum");
Check(LinuxSystemOps.DetectPackageManager(b => b == "yum") == "yum", "yum alone -> yum");
Check(LinuxSystemOps.DetectPackageManager(b => b == "zypper") == "zypper", "zypper");
Check(LinuxSystemOps.DetectPackageManager(b => b == "pacman") == "pacman", "pacman");
Check(LinuxSystemOps.DetectPackageManager(_ => false) is null, "nothing installed -> null");

// ---------------- Parsers ----------------
Console.WriteLine("[linux] output parsers (Controller column names)");
var services = Rows(LinuxSystemOps.ParseServices(
    "ssh.service loaded active running OpenBSD Secure Shell server\n" +
    "cron.service loaded active running Regular background program processing daemon\n" +
    "apt-daily.service loaded inactive dead Daily apt download activities\n" +
    "bad.service loaded failed failed Broken \"quoted\" <b>unit</b>\n" +
    "@@HRMS@@\n" +
    "ssh.service enabled enabled\ncron.service enabled enabled\napt-daily.service static -\nbad.service masked enabled\n" +
    "getty@.service enabled enabled\nunloaded.service disabled enabled\n"));
var ssh = services.FirstOrDefault(r => r["Name"] == "ssh.service");
Check(ssh is not null && ssh["State"] == "Running" && ssh["StartMode"] == "Auto" && ssh["DisplayName"] == "OpenBSD Secure Shell server",
    "services: Name / DisplayName / State=Running / StartMode=Auto");
Check(services.First(r => r["Name"] == "apt-daily.service") is { } ad && ad["State"] == "Stopped" && ad["StartMode"] == "Manual", "services: inactive static unit -> Stopped / Manual");
Check(services.First(r => r["Name"] == "bad.service") is { } bad && bad["State"] == "Failed" && bad["StartMode"] == "Disabled" && bad["DisplayName"].Contains("<b>"),
    "services: failed masked unit -> Failed / Disabled, description kept verbatim (JSON-escaped)");
Check(services.Any(r => r["Name"] == "unloaded.service" && r["State"] == "Stopped") && !services.Any(r => r["Name"] == "getty@.service"),
    "services: unit files that are not loaded are listed; templates are skipped");

var tasks = Rows(LinuxSystemOps.ParseTasks(
    "Id=apt-daily.timer\nActiveState=active\nUnitFileState=enabled\nTriggers=apt-daily.service\nNextElapseUSecRealtime=Sat 2026-11-21 18:00:00 UTC\nLastTriggerUSec=Sat 2026-11-21 06:00:00 UTC\nDescription=Daily apt download activities\n\n" +
    "Id=fstrim.timer\nActiveState=inactive\nUnitFileState=disabled\nTriggers=fstrim.service\n\n" +
    "@@HRMS@@\n" +
    "@@HRMS@@SRC\tsystem\t/etc/crontab\nSHELL=/bin/sh\n# comment\n17 * * * * root cd / && run-parts --report /etc/cron.hourly\n\n" +
    "@@HRMS@@SRC\tuser\t/var/spool/cron/crontabs/alice\n@reboot /home/alice/start.sh\n*/5 * * * * echo \"$(date)\" >> /tmp/x\n"));
var daily = tasks.FirstOrDefault(r => r["TaskName"] == "apt-daily.timer");
Check(daily is not null && daily["Type"] == "systemd timer" && daily["State"] == "Ready" && daily["Unit"] == "apt-daily.service"
      && SystemCatalog.TargetFromRow("tasks", daily) == "systemd:apt-daily.timer"
      && LinuxSystemOps.Validate("tasks/run", SystemCatalog.TargetFromRow("tasks", daily), "") is null,
    "tasks: timer row (Type, State, Unit) and TaskPath+TaskName is a valid run target");
Check(tasks.Any(r => r["TaskName"] == "fstrim.timer" && r["State"] == "Disabled"), "tasks: disabled timer -> State=Disabled");
var cron = tasks.Where(r => r["Type"] == "cron").ToList();
Check(cron.Count == 3 && cron.All(r => r["State"] == "Read-only" && r["ReadOnly"] == "Yes"), $"tasks: 3 cron jobs, all read-only (got {cron.Count})");
Check(cron.Any(r => r["Schedule"] == "17 * * * *" && r["TaskName"].Contains("(root)") && r["Command"].StartsWith("cd /")), "tasks: system crontab user field parsed");
Check(cron.Any(r => r["Schedule"] == "@reboot" && r["TaskName"].Contains("(alice)")), "tasks: user crontab @reboot entry");
Check(cron.All(r => LinuxSystemOps.Validate("tasks/run", SystemCatalog.TargetFromRow("tasks", r), "")?.Contains("read-only") == true), "tasks: cron targets are refused as read-only");

var apt = Rows(LinuxSystemOps.ParsePending("apt", "Listing...\nopenssl/noble-updates 3.0.13-0ubuntu3.4 amd64 [upgradable from: 3.0.13-0ubuntu3.1]\nlibssl3t64/noble-updates,noble-security 3.0.13-0ubuntu3.4 amd64 [upgradable from: 3.0.13-0ubuntu3.1]\n"));
Check(apt.Count == 2 && apt[0]["Package"] == "openssl" && apt[0]["NewVersion"] == "3.0.13-0ubuntu3.4" && apt[0]["CurrentVersion"] == "3.0.13-0ubuntu3.1", "updates: apt list --upgradable");
var dnf = Rows(LinuxSystemOps.ParsePending("dnf", "\nkernel.x86_64    5.14.0-503.el9    baseos\nopenssl-libs.x86_64   1:3.2.2-6.el9   baseos\nObsoleting Packages\ngrub2.x86_64  1:2.06  baseos\n"));
Check(dnf.Count == 2 && dnf[1]["Package"] == "openssl-libs" && dnf[1]["Arch"] == "x86_64", "updates: dnf check-update (stops at Obsoleting)");
var zyp = Rows(LinuxSystemOps.ParsePending("zypper", "S | Repository | Name | Current Version | Available Version | Arch\n--+------------+------+-----------------+-------------------+-----\nv | repo-oss   | curl | 8.6.0-1.1       | 8.7.1-1.1         | x86_64\n"));
Check(zyp.Count == 1 && zyp[0]["Package"] == "curl" && zyp[0]["NewVersion"] == "8.7.1-1.1", "updates: zypper list-updates");
var pac = Rows(LinuxSystemOps.ParsePending("pacman", "linux 6.11.1.arch1-1 -> 6.11.2.arch1-1\n"));
Check(pac.Count == 1 && pac[0]["CurrentVersion"] == "6.11.1.arch1-1", "updates: pacman checkupdates");

var kv = Rows(LinuxSystemOps.ParseKeyValues("PackageManager\tapt\nKernel\t6.8.0\n"));
Check(kv.Count == 2 && kv[0]["Setting"] == "PackageManager" && kv[0]["Value"] == "apt", "status: Setting / Value table");
var net = Rows(LinuxSystemOps.ParseNetwork("L\tlo\tUNKNOWN\t00:00:00:00:00:00\nL\teth0@if5\tUP\t52:54:00:12:34:56\nA\teth0@if5\t192.168.1.20/24 fe80::1/64\n"));
Check(net.Any(r => r["Name"] == "eth0" && r["Status"] == "UP" && r["Addresses"].StartsWith("192.168.1.20")), "network: link + address merged");
var users = Rows(LinuxSystemOps.ParseUsers("root:x:0:0:root:/root:/bin/bash\ndaemon:x:1:1::/usr/sbin:/usr/sbin/nologin\nalice:x:1000:1000:Alice:/home/alice:/bin/bash\nnobody:x:65534:65534::/nonexistent:/usr/sbin/nologin\n"));
Check(users.Select(u => u["Name"]).SequenceEqual(["root", "alice"]), "users: root + regular accounts only");
var tsv = LinuxSystemOps.Tsv("PCI\tVGA compatible controller\tIntel \"UHD\" <x>\n", "Class", "Type", "FriendlyName");
Check(JsonDocument.Parse(tsv).RootElement[0].GetProperty("FriendlyName").GetString() == "Intel \"UHD\" <x>", "tsv: quotes / angle brackets survive as data");

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("==== LINUX AGENT CHECKS PASSED ====");
    return 0;
}
Console.WriteLine("==== LINUX AGENT CHECKS FAILED ====");
foreach (var f in failures) Console.WriteLine("  - " + f);
return 1;

static string Show(string s) => s.Length > 30 ? s[..30] + "…" : s.Replace("\n", "\\n");
