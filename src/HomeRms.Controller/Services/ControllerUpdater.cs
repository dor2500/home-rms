using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace HomeRms.Controller.Services;

public sealed record AvailableUpdate(string Tag, Version Version, string DownloadUrl, bool IsZip = false);

/// <summary>
/// Checks GitHub Releases (dor2500/home-rms) for newer versions of HomeRmsController
/// and performs a one-click in-place self-update & restart.
/// </summary>
public static class ControllerUpdater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/dor2500/home-rms/releases/latest";
    private const string ZipAssetName = "HomeRmsController-win-x64.zip";
    private const string ExeAssetName = "HomeRmsController.exe";

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
            string? exeUrl = null;
            foreach (var asset in assetsEl.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var uEl) ? uEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;

                if (string.Equals(name, ZipAssetName, StringComparison.OrdinalIgnoreCase))
                    return new AvailableUpdate(tag, remoteVersion, url, IsZip: true);
                if (string.Equals(name, ExeAssetName, StringComparison.OrdinalIgnoreCase))
                    exeUrl = url;
            }
            if (!string.IsNullOrWhiteSpace(exeUrl))
                return new AvailableUpdate(tag, remoteVersion, exeUrl, IsZip: false);
        }

        return null;
    }

    public static async Task DownloadAndApplyAsync(AvailableUpdate update, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            throw new InvalidOperationException("Cannot determine current executable path.");

        onProgress?.Invoke($"Downloading {update.Tag} (0%)…");

        var updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HomeRMS", "Update");
        Directory.CreateDirectory(updateDir);

        var downloadPath = Path.Combine(updateDir, update.IsZip
            ? $"HomeRmsController_{update.Tag}.zip"
            : $"HomeRmsController_{update.Tag}.exe");
        var stagedExe = Path.Combine(updateDir, $"HomeRmsController_{update.Tag}.exe");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRmsController-Updater/1.0");

        using (var resp = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var totalBytes = resp.Content.Headers.ContentLength ?? -1L;

            await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

            var buffer = new byte[65536];
            long totalRead = 0;
            int lastPct = -1;
            var sw = Stopwatch.StartNew();

            int read;
            while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;

                if (totalBytes > 0)
                {
                    var pct = (int)(totalRead * 100 / totalBytes);
                    if (pct != lastPct && (pct == 100 || sw.ElapsedMilliseconds >= 150))
                    {
                        lastPct = pct;
                        sw.Restart();
                        onProgress?.Invoke($"Downloading {pct}% ({totalRead / (1024 * 1024)}/{totalBytes / (1024 * 1024)} MB)…");
                    }
                }
                else if (sw.ElapsedMilliseconds >= 250)
                {
                    sw.Restart();
                    onProgress?.Invoke($"Downloading {totalRead / (1024 * 1024)} MB…");
                }
            }
        }

        if (update.IsZip)
        {
            onProgress?.Invoke("Extracting update…");
            var extractDir = Path.Combine(updateDir, $"extract_{update.Tag}");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
            var extracted = Path.Combine(extractDir, ExeAssetName);
            if (!File.Exists(extracted))
                throw new FileNotFoundException("HomeRmsController.exe missing from update archive.");
            File.Move(extracted, stagedExe, overwrite: true);
            try { File.Delete(downloadPath); Directory.Delete(extractDir, recursive: true); } catch { }
        }

        var info = new FileInfo(stagedExe);
        if (!info.Exists || info.Length < 100_000)
            throw new InvalidDataException("Downloaded update file is incomplete.");

        var header = new byte[2];
        await using (var check = File.OpenRead(stagedExe))
            _ = await check.ReadAsync(header, ct);

        if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new InvalidDataException("Downloaded update failed executable header verification.");

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
