using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HomeRms.Controller.Services;

public enum DeployItemStatus
{
    Pending,
    Installing,
    Success,
    Failed,
}

public sealed class WingetAppItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private DeployItemStatus _deployStatus = DeployItemStatus.Pending;
    private string _statusDetail = "";

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Publisher { get; init; }
    public required string Version { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public string IconUrl { get; init; } = "";

    public string Monogram => string.IsNullOrWhiteSpace(Name)
        ? "APP"
        : string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public DeployItemStatus DeployStatus
    {
        get => _deployStatus;
        set { _deployStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); }
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set { _statusDetail = value; OnPropertyChanged(); }
    }

    public string StatusLabel => _deployStatus switch
    {
        DeployItemStatus.Pending => "Pending",
        DeployItemStatus.Installing => "Installing...",
        DeployItemStatus.Success => "Installed",
        DeployItemStatus.Failed => "Failed",
        _ => "Pending",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed class WingetPack
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required List<string> PackageIds { get; init; }
}

/// <summary>
/// Server-side cached Winstall-style Winget software catalog, icon resolver, preset packs,
/// and PowerShell deployment script builder (with Winget auto-bootstrap).
/// </summary>
public sealed class WingetCatalogService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeRMS");
    private static readonly string CatalogCachePath = Path.Combine(CacheDir, "winget-cache.json");
    private static readonly string CustomPacksPath = Path.Combine(CacheDir, "winget-packs.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public List<WingetAppItem> Catalog { get; private set; } = [];
    public List<WingetPack> Packs { get; private set; } = [];

    public WingetCatalogService()
    {
        LoadDefaultCatalog();
        LoadPacks();
    }

    public void LoadDefaultCatalog()
    {
        if (File.Exists(CatalogCachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<List<WingetAppItem>>(File.ReadAllText(CatalogCachePath));
                if (cached is { Count: > 10 })
                {
                    Catalog = cached;
                    return;
                }
            }
            catch { }
        }

        Catalog = BuiltInApps();
        SaveCatalogCache();
    }

    public void SaveCatalogCache()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CatalogCachePath, JsonSerializer.Serialize(Catalog, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void LoadPacks()
    {
        Packs =
        [
            new WingetPack
            {
                Name = "Fresh PC Setup",
                Description = "Essential browser, compression, PDF reader, and runtime tools",
                PackageIds = ["Google.Chrome", "7zip.7zip", "Notepad++.Notepad++", "VideoLAN.VLC", "Microsoft.PowerToys"]
            },
            new WingetPack
            {
                Name = "Dev Tools",
                Description = "Git, VS Code, Windows Terminal, .NET SDK, Python, Docker",
                PackageIds = ["Git.Git", "Microsoft.VisualStudioCode", "Microsoft.WindowsTerminal", "Microsoft.DotNet.SDK.9", "Python.Python.3.12"]
            },
            new WingetPack
            {
                Name = "Admin & Diagnostics",
                Description = "Sysinternals, Wireshark, WinSCP, PuTTY, Everything Search",
                PackageIds = ["Microsoft.Sysinternals.Suite", "WiresharkFoundation.Wireshark", "WinSCP.WinSCP", "PuTTY.PuTTY", "voidtools.Everything"]
            },
            new WingetPack
            {
                Name = "Communication & Media",
                Description = "Discord, Slack, Zoom, Spotify, OBS Studio",
                PackageIds = ["Discord.Discord", "SlackTechnologies.Slack", "Zoom.Zoom", "Spotify.Spotify", "OBSProject.OBSStudio"]
            },
        ];

        if (File.Exists(CustomPacksPath))
        {
            try
            {
                var custom = JsonSerializer.Deserialize<List<WingetPack>>(File.ReadAllText(CustomPacksPath));
                if (custom is not null) Packs.AddRange(custom);
            }
            catch { }
        }
    }

    public void SaveCustomPack(string name, string description, IReadOnlyList<string> ids)
    {
        Packs.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Packs.Add(new WingetPack { Name = name, Description = description, PackageIds = [.. ids] });
        try
        {
            Directory.CreateDirectory(CacheDir);
            var customOnly = Packs.Skip(4).ToList();
            File.WriteAllText(CustomPacksPath, JsonSerializer.Serialize(customOnly, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Queries Winget live via GitHub / local winget search and caches new entries.</summary>
    public async Task<int> RefreshFromWingetAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        try
        {
            // Query winget.run community API (fast JSON index over winget-pkgs)
            var url = $"https://api.winget.run/v2/packages?ensureContains=true&query={Uri.EscapeDataString(query.Trim())}&take=24";
            var json = await Http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array)
                return 0;

            var added = 0;
            foreach (var p in pkgs.EnumerateArray())
            {
                var id = p.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id) || Catalog.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var latest = p.TryGetProperty("Latest", out var lEl) ? lEl : default;
                var name = latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("Name", out var nEl) ? nEl.GetString() ?? id : id;
                var pub = latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("Publisher", out var pubEl) ? pubEl.GetString() ?? "" : id.Split('.')[0];
                var ver = p.TryGetProperty("Versions", out var vArr) && vArr.ValueKind == JsonValueKind.Array && vArr.GetArrayLength() > 0
                    ? vArr[0].GetString() ?? "latest"
                    : "latest";
                var desc = latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("Description", out var dEl) ? dEl.GetString() ?? "" : "";
                var homepage = latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("Homepage", out var hEl) ? hEl.GetString() ?? "" : "";
                var icon = DeriveFaviconUrl(homepage);

                Catalog.Add(new WingetAppItem
                {
                    Id = id,
                    Name = name,
                    Publisher = pub,
                    Version = ver,
                    Category = "Winget",
                    Description = desc.Length > 110 ? desc[..107] + "..." : desc,
                    IconUrl = icon,
                });
                added++;
            }
            if (added > 0) SaveCatalogCache();
            return added;
        }
        catch
        {
            return 0;
        }
    }

    public static string BuildWingetInstallScript(string packageId, bool silent, bool machineScope, bool upgradeIfInstalled)
    {
        var verb = upgradeIfInstalled ? "upgrade" : "install";
        var silentFlag = silent ? "--silent" : "";
        var scopeFlag = machineScope ? "--scope machine" : "--scope user";
        return $$"""
            $ErrorActionPreference = 'Continue'
            $ProgressPreference = 'SilentlyContinue'
            $winget = Get-Command winget.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
            if (-not $winget) {
                $winget = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
            }
            if (-not $winget) {
                Write-Output 'Winget missing — bootstrapping Microsoft.DesktopAppInstaller...'
                try {
                    Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction Stop
                    $winget = 'winget.exe'
                } catch {
                    $tmp = Join-Path $env:TEMP 'Microsoft.DesktopAppInstaller.msixbundle'
                    Invoke-WebRequest -Uri 'https://aka.ms/getwinget' -OutFile $tmp -UseBasicParsing
                    Add-AppxPackage -Path $tmp
                    $winget = 'winget.exe'
                }
            }
            & $winget {{verb}} --id "{{packageId}}" -e {{silentFlag}} {{scopeFlag}} --accept-package-agreements --accept-source-agreements --disable-interactivity
            if ($LASTEXITCODE -ne 0 -and '{{verb}}' -eq 'upgrade') {
                & $winget install --id "{{packageId}}" -e {{silentFlag}} {{scopeFlag}} --accept-package-agreements --accept-source-agreements --disable-interactivity
            }
            exit $LASTEXITCODE
            """;
    }

    private static string DeriveFaviconUrl(string homepage)
    {
        if (Uri.TryCreate(homepage, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=64";
        return "";
    }

    private static List<WingetAppItem> BuiltInApps() =>
    [
        App("Google.Chrome", "Google Chrome", "Google LLC", "131.0", "Browsers", "Fast, secure web browser from Google", "https://cdn.simpleicons.org/googlechrome/4285F4"),
        App("Mozilla.Firefox", "Mozilla Firefox", "Mozilla", "133.0", "Browsers", "Privacy-focused open-source web browser", "https://cdn.simpleicons.org/firefox/FF7139"),
        App("Brave.Brave", "Brave Browser", "Brave Software", "1.73", "Browsers", "Chromium browser with built-in ad/tracker blocker", "https://cdn.simpleicons.org/brave/FB542B"),
        App("Microsoft.VisualStudioCode", "Visual Studio Code", "Microsoft", "1.95", "Developer", "Lightweight extensible source code editor", "https://cdn.simpleicons.org/visualstudiocode/007ACC"),
        App("Git.Git", "Git for Windows", "The Git Project", "2.47.1", "Developer", "Distributed version control system and Git Bash", "https://cdn.simpleicons.org/git/F05032"),
        App("Microsoft.WindowsTerminal", "Windows Terminal", "Microsoft", "1.21", "Developer", "Modern tabbed terminal for PowerShell, WSL, and CMD", "https://cdn.simpleicons.org/windowsterminal/4D4D4D"),
        App("Docker.DockerDesktop", "Docker Desktop", "Docker Inc.", "4.36", "Developer", "Build, share, and run containerized applications", "https://cdn.simpleicons.org/docker/2496ED"),
        App("Python.Python.3.12", "Python 3.12", "Python Software Foundation", "3.12.7", "Developer", "Python programming language and pip package manager", "https://cdn.simpleicons.org/python/3776AB"),
        App("OpenJS.NodeJS.LTS", "Node.js LTS", "OpenJS Foundation", "22.11", "Developer", "JavaScript runtime built on Chrome V8 engine", "https://cdn.simpleicons.org/nodedotjs/339933"),
        App("Microsoft.DotNet.SDK.9", ".NET 9 SDK", "Microsoft", "9.0.100", "Developer", "Cross-platform .NET development kit", "https://cdn.simpleicons.org/dotnet/512BD4"),
        App("7zip.7zip", "7-Zip", "Igor Pavlov", "24.08", "Utilities", "High-compression open-source file archiver", "https://cdn.simpleicons.org/7zip/ECEEF2"),
        App("Notepad++.Notepad++", "Notepad++", "Don Ho", "8.7.1", "Utilities", "Fast source code and plain-text editor", "https://cdn.simpleicons.org/notepadplusplus/90E59A"),
        App("Microsoft.PowerToys", "Microsoft PowerToys", "Microsoft", "0.86", "Utilities", "Windows power-user utilities (FancyZones, Run, ColorPicker)", "https://cdn.simpleicons.org/windows/0078D4"),
        App("voidtools.Everything", "Everything Search", "voidtools", "1.4.1", "Utilities", "Instant filename search across NTFS volumes", ""),
        App("Microsoft.Sysinternals.Suite", "Sysinternals Suite", "Microsoft", "2024.11", "Admin", "Process Explorer, Autoruns, TCPView, ProcMon diagnostics", ""),
        App("WiresharkFoundation.Wireshark", "Wireshark", "Wireshark Foundation", "4.4.2", "Admin", "Network protocol analyzer and packet capture", "https://cdn.simpleicons.org/wireshark/1679A7"),
        App("PuTTY.PuTTY", "PuTTY", "Simon Tatham", "0.82", "Admin", "Classic SSH, Telnet, and serial console client", ""),
        App("WinSCP.WinSCP", "WinSCP", "Martin Prikryl", "6.3.5", "Admin", "SFTP, FTP, and SCP graphical file manager", ""),
        App("VideoLAN.VLC", "VLC Media Player", "VideoLAN", "3.0.21", "Media", "Universal cross-platform multimedia player", "https://cdn.simpleicons.org/vlcmediaplayer/FF8800"),
        App("OBSProject.OBSStudio", "OBS Studio", "OBS Project", "30.2.3", "Media", "Screen recording and live streaming studio", "https://cdn.simpleicons.org/obsstudio/ECEEF2"),
        App("Spotify.Spotify", "Spotify", "Spotify AB", "1.2.50", "Media", "Music and podcast streaming client", "https://cdn.simpleicons.org/spotify/1DB954"),
        App("Discord.Discord", "Discord", "Discord Inc.", "1.0.9", "Communication", "Voice, video, and team chat platform", "https://cdn.simpleicons.org/discord/5865F2"),
        App("SlackTechnologies.Slack", "Slack", "Salesforce", "4.41", "Communication", "Team messaging and workspace collaboration", "https://cdn.simpleicons.org/slack/4A154B"),
        App("Zoom.Zoom", "Zoom Workplace", "Zoom Video Communications", "6.2", "Communication", "HD video conferencing and screen sharing", "https://cdn.simpleicons.org/zoom/0B5CFF"),
        App("Tailscale.Tailscale", "Tailscale", "Tailscale Inc.", "1.76", "Networking", "Zero-config WireGuard mesh VPN", "https://cdn.simpleicons.org/tailscale/ECEEF2"),
        App("AntibodySoftware.WizTree", "WizTree", "Antibody Software", "4.23", "Utilities", "Ultra-fast disk space analyzer", ""),
    ];

    private static WingetAppItem App(string id, string name, string pub, string ver, string cat, string desc, string icon) => new()
    {
        Id = id,
        Name = name,
        Publisher = pub,
        Version = ver,
        Category = cat,
        Description = desc,
        IconUrl = icon,
    };
}
