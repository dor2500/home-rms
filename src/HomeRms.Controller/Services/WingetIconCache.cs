using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HomeRms.Shared.Server;

namespace HomeRms.Controller.Services;

/// <summary>
/// Disk cache for winstall app icons (https://api.winstall.app/icons/&lt;Id&gt;.png).
/// Failures are intentionally silent: the card keeps its monogram. Misses are remembered for a week
/// and icons unused for 60 days are pruned so the cache stays small.
/// </summary>
public sealed class WingetIconCache
{
    private const string IconBase = "https://api.winstall.app/icons/";
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan UnusedTtl = TimeSpan.FromDays(60);

    private static readonly string IconDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeRMS", "winget-icons");

    private static readonly HttpClient Http = CreateHttp();
    private readonly SemaphoreSlim _downloads = new(4, 4);
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public WingetIconCache()
    {
        _ = Task.Run(Prune);
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRMS-Controller/2.1");
        return http;
    }

    /// <summary>Loads the icon (disk first, then network) and assigns it to the item on the UI thread.</summary>
    public async void Attach(WingetAppItem item)
    {
        if (item.Icon is not null || !WingetRules.IsValidPackageId(item.Id)) return;
        try
        {
            var task = _inFlight.GetOrAdd(item.Id, id => Task.Run(() => LoadAsync(id)));
            var img = await task;
            _inFlight.TryRemove(item.Id, out _);
            if (img is not null) item.Icon = img;
        }
        catch
        {
            // Silent by design: the monogram stays visible.
        }
    }

    private async Task<ImageSource?> LoadAsync(string id)
    {
        var png = Path.Combine(IconDir, id + ".png");
        var miss = Path.Combine(IconDir, id + ".miss");

        if (File.Exists(png))
        {
            try { File.SetLastAccessTimeUtc(png, DateTime.UtcNow); } catch (IOException) { }
            var cached = Decode(png);
            if (cached is not null) return cached;
            TryDelete(png);
        }
        if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < MissTtl)
            return null;

        await _downloads.WaitAsync();
        try
        {
            Directory.CreateDirectory(IconDir);
            using var resp = await Http.GetAsync(IconBase + Uri.EscapeDataString(id) + ".png");
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                await File.WriteAllTextAsync(miss, "");
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length is 0 or > 512 * 1024) return null;
            var tmp = png + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, png, overwrite: true);
            TryDelete(miss);
            return Decode(png);
        }
        finally
        {
            _downloads.Release();
        }
    }

    private static ImageSource? Decode(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 96; // sharp at 72 px on the app page
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static void Prune()
    {
        try
        {
            if (!Directory.Exists(IconDir)) return;
            var cutoff = DateTime.UtcNow - UnusedTtl;
            foreach (var f in new DirectoryInfo(IconDir).EnumerateFiles())
            {
                var stale = f.Extension == ".miss" ? f.LastWriteTimeUtc < DateTime.UtcNow - MissTtl
                          : f.Extension == ".tmp" || Max(f.LastAccessTimeUtc, f.LastWriteTimeUtc) < cutoff;
                if (stale) TryDelete(f.FullName);
            }
        }
        catch
        {
            // Best-effort housekeeping.
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
