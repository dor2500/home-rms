using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;

namespace HomeRms.Agent.Core;

/// <summary>
/// Periodically checks GitHub Releases (dor2500/home-rms) for a newer HomeRmsAgent.exe
/// and automatically downloads, replaces the running binary, and restarts the Agent
/// on the end-user's PC with zero manual intervention.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentUpdater(ActivityLog log, string[] launchArgs) : IDisposable
{
    private const string LatestReleaseApi = "https://api.github.com/repos/dor2500/home-rms/releases/latest";
    private const string AssetName = "HomeRmsAgent.exe";

    private readonly CancellationTokenSource _cts = new();

    public void Start()
    {
        if (launchArgs.Any(a => string.Equals(a, "--no-auto-update", StringComparison.OrdinalIgnoreCase)))
        {
            log.Write("[updater] auto-update disabled via --no-auto-update");
            return;
        }

        // Only auto-replace when running as a published single-file HomeRmsAgent.exe (not dotnet run)
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) ||
            !string.Equals(Path.GetFileName(exePath), AssetName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = Task.Run(() => LoopAsync(exePath, _cts.Token));
    }

    private async Task LoopAsync(string exePath, CancellationToken ct)
    {
        // Wait 6 seconds after startup so initial boot & discovery are instant
        try { await Task.Delay(TimeSpan.FromSeconds(6), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAndApplyOnceAsync(exePath, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.Write($"[updater] check skipped: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(15), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAndApplyOnceAsync(string exePath, CancellationToken ct)
    {
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRmsAgent-AutoUpdater/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = await http.GetStringAsync(LatestReleaseApi, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        if (!TryParseReleaseVersion(tag, out var remoteVersion))
            return;

        if (remoteVersion <= new Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build))
            return;

        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out var uEl) ? uEl.GetString() : null;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
            return;

        log.Write($"[updater] new release {tag} found (current v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}) — downloading...");

        var updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HomeRMS", "Update");
        Directory.CreateDirectory(updateDir);

        var stagedExe = Path.Combine(updateDir, $"HomeRmsAgent_{tag}.exe");
        var bytes = await http.GetByteArrayAsync(downloadUrl, ct);

        // Verify valid Windows PE executable ("MZ") and reasonable size (>100 KB)
        if (bytes.Length < 100_000 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            log.Write("[updater] downloaded file failed PE header check — aborting update");
            return;
        }

        await File.WriteAllBytesAsync(stagedExe, bytes, ct);

        var scriptPath = Path.Combine(updateDir, "apply-update.ps1");
        var pid = Environment.ProcessId;
        var preservedArgs = string.Join(" ", launchArgs
            .Where(a => !string.Equals(a, "--elevate", StringComparison.OrdinalIgnoreCase))
            .Select(a => $"\"{a.Replace("\"", "\\\"")}\""));

        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
for ($i = 0; $i -lt 40; $i++) {{
    if (-not (Get-Process -Id {pid} -ErrorAction SilentlyContinue)) {{ break }}
    Start-Sleep -Milliseconds 250
}}
Copy-Item -LiteralPath '{stagedExe.Replace("'", "''")}' -Destination '{exePath.Replace("'", "''")}' -Force
Remove-Item -LiteralPath '{stagedExe.Replace("'", "''")}' -Force
Start-Process -FilePath '{exePath.Replace("'", "''")}' {(string.IsNullOrWhiteSpace(preservedArgs) ? "" : $"-ArgumentList '{preservedArgs.Replace("'", "''")}'")}
";
        await File.WriteAllTextAsync(scriptPath, script, ct);

        log.Write($"[updater] applying update to {tag} and restarting agent...");
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Environment.Exit(0);
    }

    private static bool TryParseReleaseVersion(string tag, out Version version)
    {
        var clean = tag.Trim().TrimStart('v', 'V');
        var dash = clean.IndexOf('-');
        if (dash >= 0) clean = clean[..dash];
        return Version.TryParse(clean, out version!);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
