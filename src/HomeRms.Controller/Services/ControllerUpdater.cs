using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace HomeRms.Controller.Services;

public sealed record AvailableUpdate(string Tag, Version Version, string DownloadUrl);

/// <summary>
/// Checks GitHub Releases (dor2500/home-rms) for newer versions of HomeRmsController
/// and performs a one-click in-place self-update & restart.
/// </summary>
public static class ControllerUpdater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/dor2500/home-rms/releases/latest";
    private const string AssetName = "HomeRmsController.exe";

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    public static async Task<AvailableUpdate?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRmsController-Updater/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = await http.GetStringAsync(LatestReleaseApi, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        if (!TryParseReleaseVersion(tag, out var remoteVersion))
            return null;

        if (remoteVersion <= CurrentVersion)
            return null;

        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    var url = asset.TryGetProperty("browser_download_url", out var uEl) ? uEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                        return new AvailableUpdate(tag, remoteVersion, url);
                }
            }
        }

        return null;
    }

    public static async Task DownloadAndApplyAsync(AvailableUpdate update, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            throw new InvalidOperationException("Cannot determine current executable path.");

        onProgress?.Invoke($"Downloading {update.Tag}…");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRmsController-Updater/1.0");
        var bytes = await http.GetByteArrayAsync(update.DownloadUrl, ct);

        if (bytes.Length < 100_000 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidDataException("Downloaded update failed executable header verification.");

        var updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HomeRMS", "Update");
        Directory.CreateDirectory(updateDir);

        var stagedExe = Path.Combine(updateDir, $"HomeRmsController_{update.Tag}.exe");
        await File.WriteAllBytesAsync(stagedExe, bytes, ct);

        var scriptPath = Path.Combine(updateDir, "apply-controller-update.ps1");
        var pid = Environment.ProcessId;
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
for ($i = 0; $i -lt 40; $i++) {{
    if (-not (Get-Process -Id {pid} -ErrorAction SilentlyContinue)) {{ break }}
    Start-Sleep -Milliseconds 250
}}
Copy-Item -LiteralPath '{stagedExe.Replace("'", "''")}' -Destination '{exePath.Replace("'", "''")}' -Force
Remove-Item -LiteralPath '{stagedExe.Replace("'", "''")}' -Force
$uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HomeRmsController'
if (Test-Path $uninstKey) {{
    Set-ItemProperty -Path $uninstKey -Name 'DisplayVersion' -Value '{update.Version}'
}}
Start-Process -FilePath '{exePath.Replace("'", "''")}'
";
        await File.WriteAllTextAsync(scriptPath, script, ct);

        onProgress?.Invoke("Restarting with new version…");
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static bool TryParseReleaseVersion(string tag, out Version version)
    {
        var clean = tag.Trim().TrimStart('v', 'V');
        var dash = clean.IndexOf('-');
        if (dash >= 0) clean = clean[..dash];
        return Version.TryParse(clean, out version!);
    }
}
