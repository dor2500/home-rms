using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HomeRms.Controller.Models;
using Renci.SshNet;

namespace HomeRms.Controller.Services;

public sealed record ApplianceMetrics(
    bool Online,
    int LatencyMs,
    string StatusText,
    double CpuPercent,
    double RamPercent,
    string DiskSummary,
    string ServicesSummary);

/// <summary>
/// Manages Linux servers, NAS appliances, Home Assistant instances, and local Web Services
/// via Ping, HTTP health checks, and SSH.NET (password or private key).
/// </summary>
public sealed class SshApplianceService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    {
        Timeout = TimeSpan.FromSeconds(4),
    };

    public async Task<ApplianceMetrics> ProbeAsync(MachineProfile profile, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var latency = 0;
        var pingOk = false;

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(profile.Host, 1200);
            if (reply.Status == IPStatus.Success)
            {
                pingOk = true;
                latency = (int)reply.RoundtripTime;
            }
        }
        catch { }

        // HTTP check for Home Assistant / Web Services / NAS web UI
        if (!string.IsNullOrWhiteSpace(profile.WebUrl))
        {
            try
            {
                using var res = await Http.GetAsync(profile.WebUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                if (latency == 0) latency = (int)sw.ElapsedMilliseconds;
                var httpStatus = $"HTTP {(int)res.StatusCode} ({res.StatusCode})";
                if (string.IsNullOrWhiteSpace(profile.Username))
                    return new ApplianceMetrics(true, latency, httpStatus, 0, 0, "--", httpStatus);
            }
            catch (Exception ex) when (profile.Kind is EndpointKind.HomeAssistant or EndpointKind.WebService && string.IsNullOrWhiteSpace(profile.Username))
            {
                return new ApplianceMetrics(pingOk, latency, pingOk ? "Ping OK (HTTP down)" : ex.Message, 0, 0, "--", "Unreachable");
            }
        }

        // SSH metrics for Linux / NAS if credentials are configured
        if (!string.IsNullOrWhiteSpace(profile.Username))
        {
            try
            {
                var metrics = await Task.Run(() => FetchSshMetrics(profile, latency), ct);
                return metrics;
            }
            catch (Exception ex)
            {
                return new ApplianceMetrics(pingOk, latency, pingOk ? $"Ping OK (SSH: {ex.Message})" : ex.Message, 0, 0, "--", "SSH error");
            }
        }

        // TCP port fallback check
        if (!pingOk && profile.Port > 0)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(profile.Host, profile.Port, ct);
                sw.Stop();
                return new ApplianceMetrics(true, (int)sw.ElapsedMilliseconds, $"TCP :{profile.Port} Open", 0, 0, "--", "Port Open");
            }
            catch { }
        }

        return new ApplianceMetrics(pingOk, latency, pingOk ? "ICMP Reachable" : "Offline", 0, 0, "--", "--");
    }

    public Task<(bool Ok, string Output)> ExecuteSshAsync(MachineProfile profile, string command, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var client = CreateClient(profile);
                client.Connect();
                using var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(30);
                var stdout = cmd.Execute();
                var stderr = cmd.Error;
                client.Disconnect();
                var combined = string.IsNullOrWhiteSpace(stderr)
                    ? stdout
                    : string.IsNullOrWhiteSpace(stdout) ? stderr : $"{stdout.TrimEnd()}\n{stderr}";
                return (cmd.ExitStatus == 0, combined);
            }
            catch (Exception ex)
            {
                return (false, $"SSH error: {ex.Message}");
            }
        }, ct);
    }

    private static ApplianceMetrics FetchSshMetrics(MachineProfile profile, int latency)
    {
        using var client = CreateClient(profile);
        var sw = Stopwatch.StartNew();
        client.Connect();
        sw.Stop();
        if (latency == 0) latency = (int)sw.ElapsedMilliseconds;

        const string probeCmd = """
            CPU=$(top -bn1 2>/dev/null | awk '/Cpu\(s\)/ {print 100 - $8; exit}')
            if [ -z "$CPU" ]; then CPU=$(awk '{print $1*10}' /proc/loadavg 2>/dev/null); fi
            RAM=$(free -m 2>/dev/null | awk '/Mem:/ {if($2>0) printf "%.1f", $3/$2*100; else print "0"}')
            DISK=$(df -h / 2>/dev/null | awk 'NR==2 {print $3 "/" $2 " (" $5 ")"}')
            DOCK=$(command -v docker >/dev/null 2>&1 && docker ps -q 2>/dev/null | wc -l || echo "n/a")
            OS=$(grep -E '^PRETTY_NAME=' /etc/os-release 2>/dev/null | cut -d= -f2 | tr -d '"')
            echo "CPU=$CPU"
            echo "RAM=$RAM"
            echo "DISK=$DISK"
            echo "DOCK=$DOCK"
            echo "OS=$OS"
            """;

        using var cmd = client.CreateCommand(probeCmd);
        cmd.CommandTimeout = TimeSpan.FromSeconds(8);
        var output = cmd.Execute();
        client.Disconnect();

        double cpu = 0, ram = 0;
        var disk = "--";
        var dock = "n/a";
        var os = "Linux";
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var k = line[..idx].Trim();
            var v = line[(idx + 1)..].Trim();
            if (k == "CPU") double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cpu);
            else if (k == "RAM") double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ram);
            else if (k == "DISK" && !string.IsNullOrEmpty(v)) disk = v;
            else if (k == "DOCK" && !string.IsNullOrEmpty(v)) dock = v;
            else if (k == "OS" && !string.IsNullOrEmpty(v)) os = v;
        }

        var svc = dock == "n/a" ? $"SSH Connected ({os})" : $"Docker: {dock} running ({os})";
        return new ApplianceMetrics(true, latency, "SSH Connected", Math.Clamp(cpu, 0, 100), Math.Clamp(ram, 0, 100), disk, svc);
    }

    private static SshClient CreateClient(MachineProfile profile)
    {
        var port = profile.SshPort > 0 ? profile.SshPort : 22;
        var user = string.IsNullOrWhiteSpace(profile.Username) ? "root" : profile.Username;
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(profile.SshKeyPath) && File.Exists(profile.SshKeyPath))
        {
            var pass = profile.GetPlainPassword();
            var keyFile = string.IsNullOrEmpty(pass)
                ? new PrivateKeyFile(profile.SshKeyPath)
                : new PrivateKeyFile(profile.SshKeyPath, pass);
            methods.Add(new PrivateKeyAuthenticationMethod(user, keyFile));
        }

        var plainPass = profile.GetPlainPassword();
        if (!string.IsNullOrEmpty(plainPass))
            methods.Add(new PasswordAuthenticationMethod(user, plainPass));

        if (methods.Count == 0)
            methods.Add(new PasswordAuthenticationMethod(user, ""));

        var connInfo = new ConnectionInfo(profile.Host, port, user, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(6),
        };
        return new SshClient(connInfo);
    }
}
