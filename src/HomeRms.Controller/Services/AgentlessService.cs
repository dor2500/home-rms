using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HomeRms.Controller.Models;

namespace HomeRms.Controller.Services;

public sealed record AgentlessDisk(string Name, double TotalGb, double FreeGb);
public sealed record AgentlessApp(string Name, string Version, string Publisher);
public sealed record AgentlessInventory(string MachineName, string OsVersion, string CpuName, string GpuName, double TotalRamGb, List<AgentlessDisk> Disks, List<AgentlessApp> Apps);
public sealed record AgentlessUpdate(string UpdateId, string Title, string Kb, double SizeMb, bool RebootRequired);

/// <summary>
/// Executes management operations against Agentless Windows endpoints over WinRM (WSMan/CIM),
/// SMB, and embedded/launched RDP with DPAPI-protected credentials.
/// </summary>
public sealed class AgentlessService
{
    public async Task<(bool Ok, string Message, double Cpu, double Ram, string Os, string User, int LatencyMs)> ProbeAsync(
        MachineProfile profile, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(profile.Host, 1200);
            var latency = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : 0;

            var script = """
                $os = Get-CimInstance Win32_OperatingSystem
                $cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
                $total = [double]$os.TotalVisibleMemorySize
                $free = [double]$os.FreePhysicalMemory
                $ram = if ($total -gt 0) { [math]::Round((($total - $free) / $total) * 100, 1) } else { 0 }
                $usr = (Get-CimInstance Win32_ComputerSystem).UserName
                [PSCustomObject]@{
                    Os = "$($os.Caption) ($($os.Version))"
                    User = if ($usr) { $usr } else { $env:USERNAME }
                    Cpu = [double]$cpu
                    Ram = [double]$ram
                } | ConvertTo-Json -Compress
                """;

            var (ok, stdout, stderr) = await InvokeWinRmAsync(profile, script, ct, timeoutMs: 9000);
            sw.Stop();
            if (!ok)
                return (false, string.IsNullOrWhiteSpace(stderr) ? "WinRM unreachable" : stderr.Trim(), 0, 0, "Windows", "", (int)sw.ElapsedMilliseconds);

            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;
            return (true, "WinRM Ready",
                root.TryGetProperty("Cpu", out var c) ? c.GetDouble() : 0,
                root.TryGetProperty("Ram", out var r) ? r.GetDouble() : 0,
                root.TryGetProperty("Os", out var o) ? o.GetString() ?? "Windows" : "Windows",
                root.TryGetProperty("User", out var u) ? u.GetString() ?? "" : "",
                latency > 0 ? latency : (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, 0, 0, "Windows", "", (int)sw.ElapsedMilliseconds);
        }
    }

    public async Task<(bool Ok, string Output)> RunShellAsync(MachineProfile profile, string command, CancellationToken ct = default, int timeoutMs = 60000)
    {
        var (ok, stdout, stderr) = await InvokeWinRmAsync(profile, command, ct, timeoutMs: timeoutMs);
        var combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : string.IsNullOrWhiteSpace(stdout) ? stderr : $"{stdout.TrimEnd()}\n{stderr}";
        return (ok, combined);
    }

    public async Task<AgentlessInventory?> GetInventoryAsync(MachineProfile profile, CancellationToken ct = default)
    {
        var script = """
            $cs = Get-CimInstance Win32_ComputerSystem
            $os = Get-CimInstance Win32_OperatingSystem
            $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
            $gpu = Get-CimInstance Win32_VideoController | Select-Object -First 1
            $disks = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | ForEach-Object {
                [PSCustomObject]@{
                    Name = $_.DeviceID
                    TotalGb = [math]::Round($_.Size / 1GB, 1)
                    FreeGb = [math]::Round($_.FreeSpace / 1GB, 1)
                }
            }
            $apps = Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName } |
                Select-Object -First 250 DisplayName, DisplayVersion, Publisher |
                ForEach-Object {
                    [PSCustomObject]@{
                        Name = "$($_.DisplayName)"
                        Version = "$($_.DisplayVersion)"
                        Publisher = "$($_.Publisher)"
                    }
                }
            [PSCustomObject]@{
                Hostname = $cs.Name
                Os = "$($os.Caption) $($os.OSArchitecture)"
                Cpu = "$($cpu.Name)"
                Gpu = "$($gpu.Name)"
                RamGb = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
                Disks = @($disks)
                Apps = @($apps)
            } | ConvertTo-Json -Depth 4 -Compress
            """;

        var (ok, stdout, _) = await InvokeWinRmAsync(profile, script, ct, timeoutMs: 25000);
        if (!ok || string.IsNullOrWhiteSpace(stdout)) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;
            var diskList = new List<AgentlessDisk>();
            var appList = new List<AgentlessApp>();

            if (root.TryGetProperty("Disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in disks.EnumerateArray())
                {
                    diskList.Add(new AgentlessDisk(
                        d.TryGetProperty("Name", out var dn) ? dn.GetString() ?? "" : "",
                        d.TryGetProperty("TotalGb", out var dt) ? dt.GetDouble() : 0,
                        d.TryGetProperty("FreeGb", out var df) ? df.GetDouble() : 0));
                }
            }

            if (root.TryGetProperty("Apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in apps.EnumerateArray())
                {
                    appList.Add(new AgentlessApp(
                        a.TryGetProperty("Name", out var an) ? an.GetString() ?? "" : "",
                        a.TryGetProperty("Version", out var av) ? av.GetString() ?? "" : "",
                        a.TryGetProperty("Publisher", out var ap) ? ap.GetString() ?? "" : ""));
                }
            }

            return new AgentlessInventory(
                root.TryGetProperty("Hostname", out var hn) ? hn.GetString() ?? profile.Host : profile.Host,
                root.TryGetProperty("Os", out var os) ? os.GetString() ?? "Windows" : "Windows",
                root.TryGetProperty("Cpu", out var cpu) ? cpu.GetString() ?? "" : "",
                root.TryGetProperty("Gpu", out var gpu) ? gpu.GetString() ?? "" : "",
                root.TryGetProperty("RamGb", out var ram) ? ram.GetDouble() : 0,
                diskList,
                appList);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<AgentlessUpdate>> CheckUpdatesAsync(MachineProfile profile, CancellationToken ct = default)
    {
        var script = """
            $session = New-Object -ComObject Microsoft.Update.Session
            $searcher = $session.CreateUpdateSearcher()
            $res = $searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0")
            $items = foreach ($u in $res.Updates) {
                $kb = if ($u.KBArticleIDs.Count -gt 0) { "KB" + $u.KBArticleIDs.Item(0) } else { "" }
                [PSCustomObject]@{
                    Id = $u.Identity.UpdateID
                    Title = $u.Title
                    Kb = $kb
                    SizeMb = [math]::Round($u.MaxDownloadSize / 1MB, 1)
                    IsRebootRequired = [bool]$u.RebootRequired
                }
            }
            @($items) | ConvertTo-Json -Compress
            """;

        var list = new List<AgentlessUpdate>();
        var (ok, stdout, _) = await InvokeWinRmAsync(profile, script, ct, timeoutMs: 45000);
        if (!ok || string.IsNullOrWhiteSpace(stdout)) return list;

        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                    AddUpdate(el);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddUpdate(doc.RootElement);
            }
        }
        catch { }

        return list;

        void AddUpdate(JsonElement el)
        {
            list.Add(new AgentlessUpdate(
                el.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : "",
                el.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "",
                el.TryGetProperty("Kb", out var kb) ? kb.GetString() ?? "" : "",
                el.TryGetProperty("SizeMb", out var sz) ? sz.GetDouble() : 0,
                el.TryGetProperty("IsRebootRequired", out var rb) && rb.GetBoolean()));
        }
    }

    public async Task<(bool Ok, string Message)> InstallAgentRemotelyAsync(
        MachineProfile profile, string controllerFingerprint, Action<string> onLog, CancellationToken ct = default)
    {
        onLog($"Preparing remote Agent deployment on {profile.Host} via WinRM...");
        var pin = HomeRms.Shared.Security.ControllerIdentity.NormalizeFingerprint(controllerFingerprint ?? ""); // hex only: nothing else reaches the script
        var pinArg = pin.Length == 64 ? $" --pin {pin}" : "";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $dir = 'C:\ProgramData\HomeRmsAgent'
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $exe = Join-Path $dir 'HomeRmsAgent.exe'
            Write-Output 'Downloading latest HomeRmsAgent.exe from GitHub Releases...'
            $rel = Invoke-RestMethod 'https://api.github.com/repos/dor2500/home-rms/releases/latest' -Headers @{ 'User-Agent' = 'HomeRms-Deploy' }
            $asset = $rel.assets | Where-Object { $_.name -eq 'HomeRmsAgent.exe' } | Select-Object -First 1
            if (-not $asset) { throw 'HomeRmsAgent.exe asset not found in latest release.' }
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $exe -UseBasicParsing
            Write-Output 'Adding Windows Firewall rule for TCP 7443...'
            netsh advfirewall firewall delete rule name="Home RMS Agent" | Out-Null
            netsh advfirewall firewall add rule name="Home RMS Agent" dir=in action=allow protocol=TCP localport=7443 | Out-Null
            Write-Output 'Installing Home RMS Agent as Windows Service (LocalSystem)...'
            & $exe --install-service{{pinArg}} --auto-consent
            Write-Output 'Agent service installed and started.'
            """;

        var (ok, stdout, stderr) = await InvokeWinRmAsync(profile, script, ct, timeoutMs: 120000);
        if (!string.IsNullOrWhiteSpace(stdout)) onLog(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) onLog(stderr.Trim());
        return (ok, ok ? "Agent installed as Windows Service on port 7443." : "Remote Agent installation failed.");
    }

    public void LaunchRdpSession(MachineProfile profile, bool multimon)
    {
        var user = profile.Username;
        var pass = profile.GetPlainPassword();
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrEmpty(pass))
        {
            RunProcess("cmdkey.exe", $"/generic:TERMSRV/{profile.Host} /user:{user} /pass:\"{pass}\"");
        }

        var args = multimon ? $"/v:{profile.Host} /multimon" : $"/v:{profile.Host}";
        Process.Start(new ProcessStartInfo("mstsc.exe", args) { UseShellExecute = true });

        if (!string.IsNullOrWhiteSpace(user))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(15000);
                RunProcess("cmdkey.exe", $"/delete:TERMSRV/{profile.Host}");
            });
        }
    }

    /// <summary>
    /// PowerShell that defines <c>$cred</c> for the given account. User name and password travel as
    /// Base64 (UTF-8) literals, so no character in them (quotes, <c>'@</c>, <c>$</c>, backticks, line
    /// breaks) can end a string early or inject code.
    /// </summary>
    public static string BuildCredentialPrelude(string? user, string? password) => $$"""
        $secPass = ConvertTo-SecureString ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{B64(password)}}'))) -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential(([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{B64(user)}}'))), $secPass)
        """;

    /// <summary>
    /// The local PowerShell that runs <paramref name="scriptBody"/> on <paramref name="host"/> via
    /// Invoke-Command. The body is decoded from Base64 into a script block, so an unbalanced brace in
    /// it can never close the block early and run the remainder on THIS (controller) machine.
    /// </summary>
    public static string BuildWinRmWrapper(string? user, string? password, string host, string scriptBody) => $$"""
        $ErrorActionPreference = 'Continue'
        {{BuildCredentialPrelude(user, password)}}
        $opt = New-PSSessionOption -SkipCACheck -SkipCNCheck -SkipRevocationCheck
        $sb = [ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{B64(scriptBody)}}')))
        $target = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{B64(host)}}'))
        try {
            Invoke-Command -ComputerName $target -Credential $cred -SessionOption $opt -ScriptBlock $sb
        } catch {
            Write-Error $_
            exit 1
        }
        """;

    private static string B64(string? value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));

    public async Task<(bool Ok, string Stdout, string Stderr)> InvokeWinRmAsync(
        MachineProfile profile, string scriptBody, CancellationToken ct, int timeoutMs = 30000)
    {
        var wrapper = BuildWinRmWrapper(profile.Username, profile.GetPlainPassword(), profile.Host, scriptBody);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper));
        if (encoded.Length > 32000)
            return (false, "", $"The script is too large to send over WinRM ({scriptBody.Length:N0} characters; the limit is about 9,000).");

        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return (false, "", "Failed to start PowerShell.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            var stdout = await outTask;
            var stderr = await errTask;
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            return (false, "", "WinRM command timed out or was cancelled.");
        }
    }

    private static void RunProcess(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(3000);
        }
        catch { }
    }
}
