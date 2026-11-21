using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HomeRms.Shared.Protocol;

namespace HomeRms.Agent.Linux.Features;

/// <summary>
/// Allowlisted system-control operations on Linux (systemd services, apt/dnf/pacman updates,
/// power, ufw firewall, timers, and system info). All user values are validated and passed via
/// environment variables (never concatenated into bash strings).
/// </summary>
public static class LinuxSystemOps
{
    private sealed record Op(string Script, bool Json, TimeSpan Timeout);
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(30);

    private static readonly Dictionary<string, Op> Ops = new()
    {
        ["services/list"] = new("systemctl list-units --type=service --all --no-pager --plain --output=json 2>/dev/null || systemctl list-units --type=service --all --no-pager --plain | awk 'NR>1 && NF>=4 {printf \"{\\\"Name\\\":\\\"%s\\\",\\\"State\\\":\\\"%s\\\",\\\"Sub\\\":\\\"%s\\\"}\\n\",$1,$3,$4}' | paste -sd, - | sed 's/^/[/;s/$/]/'", true, Short),
        ["services/start"] = new("systemctl start \"$HRMS_TARGET\" && echo \"Service '$HRMS_TARGET' started.\"", false, Short),
        ["services/stop"] = new("systemctl stop \"$HRMS_TARGET\" && echo \"Service '$HRMS_TARGET' stopped.\"", false, Short),
        ["services/restart"] = new("systemctl restart \"$HRMS_TARGET\" && echo \"Service '$HRMS_TARGET' restarted.\"", false, Short),
        ["services/set-startup"] = new("if [ \"$HRMS_EXTRA\" = \"Disabled\" ]; then systemctl disable \"$HRMS_TARGET\"; else systemctl enable \"$HRMS_TARGET\"; fi", false, Short),

        ["updates/status"] = new("echo \"[{\\\"Setting\\\":\\\"PackageManager\\\",\\\"Value\\\":\\\"$(command -v apt-get || command -v dnf || command -v pacman || echo unknown)\\\"},{\\\"Setting\\\":\\\"Kernel\\\",\\\"Value\\\":\\\"$(uname -r)\\\"}]\"", true, Short),
        ["updates/list-pending"] = new("apt-get -s upgrade 2>/dev/null | grep '^Inst ' | awk '{printf \"{\\\"KB\\\":\\\"%s\\\",\\\"Title\\\":\\\"%s\\\"}\\n\",$2,$0}' | paste -sd, - | sed 's/^/[/;s/$/]/' || echo '[]'", true, Long),
        ["updates/install-all"] = new("DEBIAN_FRONTEND=noninteractive apt-get update && DEBIAN_FRONTEND=noninteractive apt-get upgrade -y", false, Long),

        ["power/restart"] = new("shutdown -r +0 'Home RMS restart requested' && echo 'Restart scheduled.'", false, Short),
        ["power/shutdown"] = new("shutdown -h +0 'Home RMS shutdown requested' && echo 'Shutdown scheduled.'", false, Short),
        ["power/cancel"] = new("shutdown -c && echo 'Pending shutdown cancelled.'", false, Short),

        ["firewall/status"] = new("ufw status verbose 2>&1 || iptables -L -n 2>&1", false, Short),
        ["firewall/enable-all"] = new("ufw --force enable", false, Short),
        ["firewall/disable-all"] = new("ufw disable", false, Short),

        ["tasks/list"] = new("systemctl list-timers --all --no-pager --plain | awk 'NR>1 && NF>=5 {printf \"{\\\"TaskName\\\":\\\"%s\\\",\\\"State\\\":\\\"%s\\\"}\\n\",$NF,$1}' | paste -sd, - | sed 's/^/[/;s/$/]/'", true, Short),
        ["system/info"] = new("printf '[{\"Setting\":\"Hostname\",\"Value\":\"%s\"},{\"Setting\":\"Kernel\",\"Value\":\"%s\"},{\"Setting\":\"Uptime\",\"Value\":\"%s\"},{\"Setting\":\"User\",\"Value\":\"%s\"}]' \"$(hostname)\" \"$(uname -srm)\" \"$(uptime -p 2>/dev/null || uptime)\" \"$(whoami)\"", true, Short),
        ["system/users"] = new("awk -F: '$3>=1000 && $1!=\"nobody\" {printf \"{\\\"Name\\\":\\\"%s\\\",\\\"UID\\\":\\\"%s\\\",\\\"Shell\\\":\\\"%s\\\"}\\n\",$1,$3,$7}' /etc/passwd | paste -sd, - | sed 's/^/[/;s/$/]/'", true, Short),
        ["system/rename"] = new("hostnamectl set-hostname \"$HRMS_TARGET\" && echo \"Hostname set to '$HRMS_TARGET'.\"", false, Short),
        ["system/timezone"] = new("timedatectl set-timezone \"$HRMS_TARGET\" && echo \"Time zone set to '$HRMS_TARGET'.\"", false, Short),
    };

    private static readonly Regex SafeUnit = new(@"^[A-Za-z0-9_.\-@:]{1,200}$", RegexOptions.Compiled);
    private static readonly Regex SafeHost = new(@"^[A-Za-z0-9\-]{1,63}$", RegexOptions.Compiled);
    private static readonly Regex SafeTz = new(@"^[A-Za-z0-9_/\-+]{1,64}$", RegexOptions.Compiled);

    public static bool IsElevated() => Environment.UserName == "root" || Environment.GetEnvironmentVariable("EUID") == "0";

    public static async Task<SystemOpResult> RunAsync(SystemOpRequest request, CancellationToken ct)
    {
        var elevated = IsElevated();
        var key = $"{request.Area}/{request.Action}";
        if (key.StartsWith("agent/update-", StringComparison.Ordinal) || key.StartsWith("agent/auto-update-", StringComparison.Ordinal))
            return new SystemOpResult
            {
                Success = false,
                Message = "Agent self-update is not supported on Linux. Update the Linux agent manually (replace the binary and restart the service).",
                IsElevated = elevated
            };
        if (!Ops.TryGetValue(key, out var op))
            return new SystemOpResult { Success = false, Message = $"Operation '{key}' is not supported on Linux.", IsElevated = elevated };

        var t = (request.Target ?? "").Trim();
        var x = (request.Extra ?? "").Trim();

        if (request.Area == "services" && request.Action != "list" && !SafeUnit.IsMatch(t))
            return new SystemOpResult { Success = false, Message = "Invalid systemd service name.", IsElevated = elevated };
        if (key == "system/rename" && !SafeHost.IsMatch(t))
            return new SystemOpResult { Success = false, Message = "Invalid hostname.", IsElevated = elevated };
        if (key == "system/timezone" && !SafeTz.IsMatch(t))
            return new SystemOpResult { Success = false, Message = "Invalid time zone (e.g. Asia/Jerusalem).", IsElevated = elevated };

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(op.Script);
        psi.Environment["HRMS_TARGET"] = t;
        psi.Environment["HRMS_EXTRA"] = x;

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

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
        var ok = proc.ExitCode == 0;

        var res = new SystemOpResult { Success = ok, IsElevated = elevated };
        if (!ok)
        {
            res.Message = string.IsNullOrWhiteSpace(stderr) ? $"Failed (exit {proc.ExitCode})." : stderr;
        }
        else if (op.Json && stdout.StartsWith('['))
        {
            res.JsonData = stdout;
            res.Message = "OK";
        }
        else
        {
            res.Message = string.IsNullOrWhiteSpace(stdout) ? "Done." : stdout;
        }
        return res;
    }
}
