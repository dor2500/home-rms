using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using HomeRms.Shared.Server;

namespace HomeRms.Controller.Services;

public enum DeployItemStatus
{
    Pending,
    Queued,
    Downloading,
    Installing,
    Success,
    Skipped,
    Failed,
    Cancelled,
}

public sealed class WingetAppItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private DeployItemStatus _deployStatus = DeployItemStatus.Pending;
    private string _statusDetail = "";
    private string _name = "";
    private string _publisher = "";
    private string _version = "";
    private string _category = "";
    private string _description = "";
    private int _percent = -1;
    private ImageSource? _icon;

    public required string Id { get; init; }
    public bool IsBuiltIn { get; init; }

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Monogram)); } }
    public string Publisher { get => _publisher; set { _publisher = value; OnPropertyChanged(); } }
    public string Version { get => _version; set { _version = value; OnPropertyChanged(); } }
    public string Category { get => _category; set { _category = value; OnPropertyChanged(); } }
    public string Description { get => _description; set { _description = value; OnPropertyChanged(); } }

    public string Monogram => string.IsNullOrWhiteSpace(Name)
        ? "APP"
        : string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    /// <summary>Cached winstall icon; null shows the monogram.</summary>
    public ImageSource? Icon { get => _icon; set { _icon = value; OnPropertyChanged(); } }

    /// <summary>Selected == in the deployment queue.</summary>
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

    public DeployItemStatus DeployStatus
    {
        get => _deployStatus;
        set
        {
            _deployStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(ProgressVisibility));
            OnPropertyChanged(nameof(IsIndeterminate));
        }
    }

    /// <summary>Exit code + message (per endpoint when several were targeted).</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        set { _statusDetail = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailVisibility)); }
    }

    /// <summary>0-100, or -1 when unknown (indeterminate bar).</summary>
    public int Percent
    {
        get => _percent;
        set { _percent = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsIndeterminate)); OnPropertyChanged(nameof(ProgressValue)); OnPropertyChanged(nameof(StatusLabel)); }
    }

    public double ProgressValue => Math.Clamp(_percent, 0, 100);
    public bool IsBusy => _deployStatus is DeployItemStatus.Queued or DeployItemStatus.Downloading or DeployItemStatus.Installing;
    public bool IsIndeterminate => IsBusy && _percent < 0;
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => string.IsNullOrEmpty(_statusDetail) ? Visibility.Collapsed : Visibility.Visible;

    public string StatusLabel => _deployStatus switch
    {
        DeployItemStatus.Queued => "Queued",
        DeployItemStatus.Downloading => _percent >= 0 ? $"Downloading {_percent}%" : "Downloading",
        DeployItemStatus.Installing => _percent >= 0 ? $"Installing {_percent}%" : "Installing",
        DeployItemStatus.Success => "Installed",
        DeployItemStatus.Skipped => "Up to date",
        DeployItemStatus.Failed => "Failed",
        DeployItemStatus.Cancelled => "Cancelled",
        _ => "Ready",
    };

    // ---------------- Install view (versions & advanced options) ----------------
    /// <summary>Latest + older versions (winstall API, else the index's latest). The first entry is "latest".</summary>
    public System.Collections.ObjectModel.ObservableCollection<VersionOption> VersionOptions { get; } = [];

    private VersionOption? _selectedVersionOption;
    public VersionOption? SelectedVersionOption
    {
        get => _selectedVersionOption;
        set { _selectedVersionOption = value; OnPropertyChanged(); OnPropertyChanged(nameof(PinnedVersion)); OptionsChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>The version to pin, or null for "latest".</summary>
    public string? PinnedVersion => _selectedVersionOption is { IsLatest: false } v ? v.Value : null;

    /// <summary>Per-app "Advanced Options" (custom configuration) used by the generated scripts and remote scope.</summary>
    public AppInstallOptions Advanced { get; } = new();

    /// <summary>True once full details (versions, licence...) were fetched from winstall.</summary>
    public bool DetailsLoaded { get; set; }

    /// <summary>Raised when the pinned version or an advanced option changes (scripts regenerate).</summary>
    public event EventHandler? OptionsChanged;

    /// <summary>Replaces the version list, keeping the current pin when it is still offered.</summary>
    public void SetVersions(IEnumerable<string> versions, string? latest)
    {
        var keep = PinnedVersion;
        var list = versions.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
        latest = string.IsNullOrWhiteSpace(latest) ? list.FirstOrDefault() ?? Version : latest;
        if (!string.IsNullOrWhiteSpace(latest)) { list.Remove(latest); list.Insert(0, latest); }
        VersionOptions.Clear();
        for (var i = 0; i < list.Count; i++) VersionOptions.Add(new VersionOption(list[i], i == 0));
        SelectedVersionOption = VersionOptions.FirstOrDefault(v => keep is not null && v.Value == keep) ?? VersionOptions.FirstOrDefault();
    }

    public void EnsureVersionOptions()
    {
        if (VersionOptions.Count == 0 && !string.IsNullOrWhiteSpace(Version)) SetVersions([Version], Version);
    }

    public WingetAppItem()
    {
        Advanced.PropertyChanged += (_, _) => OptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetDeployState()
    {
        Percent = -1;
        StatusDetail = "";
        DeployStatus = DeployItemStatus.Pending;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>An entry of the Install view's version dropdown.</summary>
public sealed record VersionOption(string Value, bool IsLatest)
{
    public string Label => IsLatest ? $"{Value} (latest)" : Value;
    public override string ToString() => Label;
}

/// <summary>Per-app custom configuration (winstall's "Advanced Options").</summary>
public sealed class AppInstallOptions : INotifyPropertyChanged
{
    private bool _useCustom;
    private int _scopeIndex;
    private bool _interactive;
    private bool _force;
    private string _override = "";
    private string _log = "";
    private string _location = "";

    public bool UseCustom { get => _useCustom; set { _useCustom = value; Notify(); } }
    /// <summary>0 = default, 1 = user, 2 = machine.</summary>
    public int ScopeIndex { get => _scopeIndex; set { _scopeIndex = Math.Clamp(value, 0, 2); Notify(); } }
    public bool Interactive { get => _interactive; set { _interactive = value; Notify(); } }
    public bool Force { get => _force; set { _force = value; Notify(); } }
    public string Override { get => _override; set { _override = value ?? ""; Notify(); } }
    public string Log { get => _log; set { _log = value ?? ""; Notify(); } }
    public string Location { get => _location; set { _location = value ?? ""; Notify(); } }

    public InstallScope Scope => (InstallScope)_scopeIndex;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed class WingetPack
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required List<string> PackageIds { get; init; }

    [JsonIgnore] public bool IsCustom { get; init; }
    [JsonIgnore] public string DisplayName => IsCustom ? $"{Name}  (custom)" : Name;
}

/// <summary>Maps winget manifest tags to Deploy panel categories.</summary>
public static class WingetCategories
{
    public const string All = "All categories";

    // Order matters: the first category whose tags match wins.
    private static readonly (string Category, string[] Tags)[] Map =
    [
        ("Browsers", ["browser", "web-browser", "chromium", "firefox", "gecko"]),
        ("Developer", ["developer-tools", "development", "programming", "ide", "sdk", "git", "compiler", "devops", "coding", "code", "debugger", "programming-language", "version-control"]),
        ("Communication", ["chat", "messaging", "messenger", "communication", "email", "mail", "voip", "video-conferencing", "meeting", "im"]),
        ("Media", ["video", "audio", "media", "music", "player", "media-player", "streaming", "podcast", "recording", "video-editor"]),
        ("Graphics", ["image", "photo", "graphics", "design", "drawing", "3d", "screenshot", "image-editor"]),
        ("Networking", ["network", "networking", "vpn", "ssh", "ftp", "sftp", "remote-desktop", "proxy", "dns", "wireguard"]),
        ("Security", ["security", "password", "password-manager", "encryption", "antivirus", "privacy", "2fa", "firewall"]),
        ("Admin", ["system", "sysadmin", "monitoring", "diagnostics", "administration", "hardware", "registry", "process"]),
        ("Productivity", ["productivity", "office", "notes", "note-taking", "pdf", "document", "markdown", "calendar", "todo"]),
        ("Gaming", ["game", "games", "gaming", "emulator", "launcher"]),
        ("AI", ["ai", "llm", "chatbot", "large-language-model", "machine-learning"]),
        ("Utilities", ["utility", "utilities", "tool", "tools", "file-manager", "compression", "archive", "zip", "backup", "disk", "clipboard", "search"]),
    ];

    public static IReadOnlyList<string> Names { get; } = [All, .. Map.Select(m => m.Category)];

    public static IReadOnlyCollection<string>? TagsFor(string category) =>
        Map.FirstOrDefault(m => m.Category == category).Tags;

    public static string FromTags(IReadOnlyCollection<string> tags)
    {
        foreach (var (cat, catTags) in Map)
            if (catTags.Any(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                return cat;
        return "Other";
    }
}

/// <summary>
/// Curated built-in catalog, the shared item registry (one object per package id so cards and the
/// queue show the same live status), preset/custom packs, and the agentless (WinRM) winget script.
/// </summary>
public sealed partial class WingetCatalogService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeRMS");
    private static readonly string LegacyCatalogCachePath = Path.Combine(CacheDir, "winget-cache.json");
    private static readonly string CustomPacksPath = Path.Combine(CacheDir, "winget-packs.json");

    private static readonly HttpClient Http = CreateHttp();

    private readonly Dictionary<string, WingetAppItem> _registry = new(StringComparer.OrdinalIgnoreCase);

    public List<WingetAppItem> BuiltIn { get; }
    public List<WingetPack> Presets { get; }
    public List<WingetPack> CustomPacks { get; private set; } = [];
    public string? PackLoadError { get; private set; }

    /// <summary>Presets followed by custom packs (combo box source).</summary>
    public List<WingetPack> Packs => [.. Presets, .. CustomPacks];

    public WingetCatalogService()
    {
        BuiltIn = BuiltInApps();
        foreach (var app in BuiltIn) _registry[app.Id] = app;
        Presets = PresetPacks();
        LoadCustomPacks();

        // The old live-search cache grew with every search; the catalog now lives in the index database.
        try { if (File.Exists(LegacyCatalogCachePath)) File.Delete(LegacyCatalogCachePath); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRMS-Controller/2.1");
        return http;
    }

    public WingetAppItem? Find(string id) => _registry.GetValueOrDefault(id);

    /// <summary>Returns the existing item for <paramref name="id"/> or registers a new one.</summary>
    public WingetAppItem GetOrCreate(string id, Func<WingetAppItem> factory)
    {
        if (_registry.TryGetValue(id, out var existing)) return existing;
        var item = factory();
        _registry[item.Id] = item;
        return item;
    }

    public WingetAppItem FromIndex(IndexPackage p) => GetOrCreate(p.Id, () => new WingetAppItem
    {
        Id = p.Id,
        Name = p.Name,
        Publisher = !string.IsNullOrWhiteSpace(p.NormPublisher) ? p.NormPublisher : PublisherFromId(p.Id),
        Version = p.Version,
        Category = WingetCategories.FromTags(p.Tags),
        Description = DescribeTags(p),
    });

    /// <summary>Registers (or enriches) an app listed by api.winstall.app.</summary>
    public WingetAppItem FromWinstall(WinstallApp a)
    {
        var item = GetOrCreate(a.Id, () => new WingetAppItem
        {
            Id = a.Id,
            Name = a.Name,
            Publisher = a.Publisher,
            Version = a.LatestVersion,
            Category = CategoryFromWinstall(a.Categories, a.Tags),
            Description = a.Description,
        });
        if (!item.IsBuiltIn)
        {
            if (a.Description.Length > 0 && !item.DetailsLoaded) item.Description = a.Description;
            if (a.LatestVersion.Length > 0) item.Version = a.LatestVersion;
        }
        return item;
    }

    /// <summary>Registers an app known only from a pack listing (id, name, publisher).</summary>
    public WingetAppItem FromPackApp(WinstallPackApp a) => GetOrCreate(a.Id, () => new WingetAppItem
    {
        Id = a.Id,
        Name = a.Name,
        Publisher = string.IsNullOrWhiteSpace(a.Publisher) ? PublisherFromId(a.Id) : a.Publisher,
        Version = a.Version ?? "",
        Category = "Other",
        Description = "",
    });

    private static string CategoryFromWinstall(IReadOnlyList<string> categories, IReadOnlyList<string> tags)
    {
        var slug = categories.FirstOrDefault();
        return DeployCategory.Find(slug)?.Label ?? WingetCategories.FromTags(tags.Select(t => t.ToLowerInvariant()).ToList());
    }

    /// <summary>Unregisters search results that are no longer shown, queued or built-in (keeps memory flat).</summary>
    public void Prune(IEnumerable<WingetAppItem> keep)
    {
        var keepSet = keep.ToHashSet();
        foreach (var (id, item) in _registry.ToList())
            if (!item.IsBuiltIn && !item.IsSelected && !item.IsBusy && !keepSet.Contains(item))
                _registry.Remove(id);
    }

    public static string PublisherFromId(string id)
    {
        var dot = id.IndexOf('.');
        return dot > 0 ? id[..dot] : id;
    }

    private static string DescribeTags(IndexPackage p)
    {
        var tags = p.Tags.Where(t => t.All(c => c < 128)).Take(5).ToList();
        var moniker = string.IsNullOrEmpty(p.Moniker) ? "" : $"winget: {p.Moniker}";
        return tags.Count == 0 ? (moniker.Length > 0 ? moniker : p.Id) : $"{(moniker.Length > 0 ? moniker + " · " : "")}{string.Join(", ", tags)}";
    }

    // ---------------- Packs ----------------
    private void LoadCustomPacks()
    {
        CustomPacks = [];
        PackLoadError = null;
        if (!File.Exists(CustomPacksPath)) return;
        try
        {
            // Same file and shape as v2.0 (a JSON array of { Name, Description, PackageIds }).
            var custom = JsonSerializer.Deserialize<List<StoredPack>>(File.ReadAllText(CustomPacksPath)) ?? [];
            CustomPacks = custom
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new WingetPack
                {
                    Name = p.Name!.Trim(),
                    Description = p.Description ?? "",
                    PackageIds = (p.PackageIds ?? []).Where(WingetRules.IsValidPackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    IsCustom = true,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            PackLoadError = $"Could not read saved packs ({CustomPacksPath}): {ex.Message}";
        }
    }

    /// <summary>Saves (or replaces) a custom pack. Throws on invalid input or I/O failure.</summary>
    public WingetPack SaveCustomPack(string name, IReadOnlyList<string> ids)
    {
        name = name.Trim();
        if (name.Length is 0 or > 60) throw new ArgumentException("Pack name must be 1-60 characters.");
        var valid = ids.Where(WingetRules.IsValidPackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (valid.Count == 0) throw new ArgumentException("The queue has no valid package IDs to save.");

        var pack = new WingetPack { Name = name, Description = $"{valid.Count} apps", PackageIds = valid, IsCustom = true };
        var list = CustomPacks.Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(pack);
        WriteCustomPacks(list);
        CustomPacks = list;
        return pack;
    }

    public void DeleteCustomPack(string name)
    {
        var list = CustomPacks.Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        WriteCustomPacks(list);
        CustomPacks = list;
    }

    private static void WriteCustomPacks(List<WingetPack> packs)
    {
        var stored = packs.Select(p => new StoredPack { Name = p.Name, Description = p.Description, PackageIds = p.PackageIds }).ToList();
        HomeRms.Shared.AtomicFile.WriteAllText(CustomPacksPath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class StoredPack
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? PackageIds { get; set; }
    }

    // ---------------- Fallback live search (community mirror, frozen since 2023) ----------------
    /// <summary>api.winget.run fallback used only when the official index is unavailable. Throws on failure.</summary>
    public async Task<List<WingetAppItem>> SearchWingetRunAsync(string query, CancellationToken ct)
    {
        var url = $"https://api.winget.run/v2/packages?ensureContains=true&query={Uri.EscapeDataString(query.Trim())}&take=50";
        var json = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("api.winget.run returned an unexpected response.");

        var items = new List<WingetAppItem>();
        foreach (var p in pkgs.EnumerateArray())
        {
            var id = p.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (!WingetRules.IsValidPackageId(id)) continue;
            var latest = p.TryGetProperty("Latest", out var lEl) && lEl.ValueKind == JsonValueKind.Object ? lEl : default;
            string Get(string prop) => latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            var tags = latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("Tags", out var tEl) && tEl.ValueKind == JsonValueKind.Array
                ? tEl.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!.ToLowerInvariant()).ToList()
                : [];
            var ver = p.TryGetProperty("Versions", out var vArr) && vArr.ValueKind == JsonValueKind.Array && vArr.GetArrayLength() > 0
                ? vArr[0].GetString() ?? "" : "";
            var desc = Get("Description");
            items.Add(GetOrCreate(id, () => new WingetAppItem
            {
                Id = id,
                Name = Get("Name") is { Length: > 0 } n ? n : id,
                Publisher = Get("Publisher") is { Length: > 0 } pub ? pub : PublisherFromId(id),
                Version = ver,
                Category = WingetCategories.FromTags(tags),
                Description = desc.Length > 110 ? desc[..107] + "..." : desc,
            }));
        }
        return items;
    }

    // ---------------- Agentless (WinRM) script ----------------
    public const string ExitMarker = "HOMERMS_WINGET_EXIT=";

    [GeneratedRegex(@"HOMERMS_WINGET_EXIT=(-?\d+)")]
    private static partial Regex ExitMarkerRegex();

    /// <summary>Returns the winget exit code printed by <see cref="BuildWingetInstallScript"/>, or null when missing.</summary>
    public static int? ParseExitMarker(string output)
    {
        var m = ExitMarkerRegex().Matches(output);
        return m.Count > 0 && int.TryParse(m[^1].Groups[1].Value, out var code) ? code : null;
    }

    /// <summary>
    /// PowerShell for WinRM endpoints (no agent). The ID is validated with <see cref="WingetRules"/> and the
    /// script prints <see cref="ExitMarker"/>&lt;code&gt; as its last line so the real exit code survives WinRM.
    /// </summary>
    public static string BuildWingetInstallScript(string packageId, bool silent, bool machineScope, bool upgradeIfInstalled, string? version = null)
    {
        if (!WingetRules.IsValidPackageId(packageId))
            throw new ArgumentException($"Invalid winget package id '{packageId}'.", nameof(packageId));
        if (!string.IsNullOrEmpty(version) && !WingetRules.IsValidVersion(version))
            throw new ArgumentException($"Invalid version '{version}'.", nameof(version));

        var pin = string.IsNullOrEmpty(version) ? "" : $" --version '{version}'";
        var common = $"--id '{packageId}' -e{pin}{(silent ? " --silent" : "")}{(machineScope ? " --scope machine" : "")} --accept-package-agreements --accept-source-agreements --disable-interactivity";
        var noPackage = unchecked((int)WingetRules.NoPackageFound);
        var upgradeBlock = upgradeIfInstalled
            ? $$"""
                & $winget upgrade {{common}}
                $code = $LASTEXITCODE
                if ($code -eq {{noPackage}}) {
                    & $winget install {{common}}
                    $code = $LASTEXITCODE
                }
                """
            : $$"""
                & $winget install {{common}}
                $code = $LASTEXITCODE
                """;

        return $$"""
            $ErrorActionPreference = 'Continue'
            $ProgressPreference = 'SilentlyContinue'
            $winget = Get-Command winget.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
            if (-not $winget) {
                $winget = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
            }
            if (-not $winget) {
                Write-Output 'winget missing - bootstrapping Microsoft.DesktopAppInstaller...'
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
            $code = -1
            {{upgradeBlock}}
            Write-Output "{{ExitMarker}}$code"
            exit $code
            """;
    }

    // ---------------- Built-in data ----------------
    private static List<WingetPack> PresetPacks() =>
    [
        new WingetPack
        {
            Name = "Fresh PC Setup",
            Description = "Essential browser, compression, editor, media player and power tools",
            PackageIds = ["Google.Chrome", "7zip.7zip", "Notepad++.Notepad++", "VideoLAN.VLC", "Microsoft.PowerToys"],
        },
        new WingetPack
        {
            Name = "Dev Tools",
            Description = "Git, VS Code, Windows Terminal, .NET SDK, Python",
            PackageIds = ["Git.Git", "Microsoft.VisualStudioCode", "Microsoft.WindowsTerminal", "Microsoft.DotNet.SDK.9", "Python.Python.3.12"],
        },
        new WingetPack
        {
            Name = "Admin & Diagnostics",
            Description = "Sysinternals, Wireshark, WinSCP, PuTTY, Everything Search",
            PackageIds = ["Microsoft.Sysinternals.Suite", "WiresharkFoundation.Wireshark", "WinSCP.WinSCP", "PuTTY.PuTTY", "voidtools.Everything"],
        },
        new WingetPack
        {
            Name = "Communication & Media",
            Description = "Discord, Slack, Zoom, Spotify, OBS Studio",
            PackageIds = ["Discord.Discord", "SlackTechnologies.Slack", "Zoom.Zoom", "Spotify.Spotify", "OBSProject.OBSStudio"],
        },
    ];

    private static List<WingetAppItem> BuiltInApps() =>
    [
        App("Google.Chrome", "Google Chrome", "Google LLC", "131.0", "Browsers", "Fast, secure web browser from Google"),
        App("Mozilla.Firefox", "Mozilla Firefox", "Mozilla", "133.0", "Browsers", "Privacy-focused open-source web browser"),
        App("Brave.Brave", "Brave Browser", "Brave Software", "1.73", "Browsers", "Chromium browser with built-in ad/tracker blocker"),
        App("Microsoft.VisualStudioCode", "Visual Studio Code", "Microsoft", "1.95", "Developer", "Lightweight extensible source code editor"),
        App("Git.Git", "Git for Windows", "The Git Project", "2.47.1", "Developer", "Distributed version control system and Git Bash"),
        App("Microsoft.WindowsTerminal", "Windows Terminal", "Microsoft", "1.21", "Developer", "Modern tabbed terminal for PowerShell, WSL, and CMD"),
        App("Docker.DockerDesktop", "Docker Desktop", "Docker Inc.", "4.36", "Developer", "Build, share, and run containerized applications"),
        App("Python.Python.3.12", "Python 3.12", "Python Software Foundation", "3.12.7", "Developer", "Python programming language and pip package manager"),
        App("OpenJS.NodeJS.LTS", "Node.js LTS", "OpenJS Foundation", "22.11", "Developer", "JavaScript runtime built on Chrome V8 engine"),
        App("Microsoft.DotNet.SDK.9", ".NET 9 SDK", "Microsoft", "9.0.100", "Developer", "Cross-platform .NET development kit"),
        App("7zip.7zip", "7-Zip", "Igor Pavlov", "24.08", "Utilities", "High-compression open-source file archiver"),
        App("Notepad++.Notepad++", "Notepad++", "Don Ho", "8.7.1", "Utilities", "Fast source code and plain-text editor"),
        App("Microsoft.PowerToys", "Microsoft PowerToys", "Microsoft", "0.86", "Utilities", "Windows power-user utilities (FancyZones, Run, ColorPicker)"),
        App("voidtools.Everything", "Everything Search", "voidtools", "1.4.1", "Utilities", "Instant filename search across NTFS volumes"),
        App("Microsoft.Sysinternals.Suite", "Sysinternals Suite", "Microsoft", "2024.11", "Admin", "Process Explorer, Autoruns, TCPView, ProcMon diagnostics"),
        App("WiresharkFoundation.Wireshark", "Wireshark", "Wireshark Foundation", "4.4.2", "Admin", "Network protocol analyzer and packet capture"),
        App("PuTTY.PuTTY", "PuTTY", "Simon Tatham", "0.82", "Admin", "Classic SSH, Telnet, and serial console client"),
        App("WinSCP.WinSCP", "WinSCP", "Martin Prikryl", "6.3.5", "Admin", "SFTP, FTP, and SCP graphical file manager"),
        App("VideoLAN.VLC", "VLC Media Player", "VideoLAN", "3.0.21", "Media", "Universal cross-platform multimedia player"),
        App("OBSProject.OBSStudio", "OBS Studio", "OBS Project", "30.2.3", "Media", "Screen recording and live streaming studio"),
        App("Spotify.Spotify", "Spotify", "Spotify AB", "1.2.50", "Media", "Music and podcast streaming client"),
        App("Discord.Discord", "Discord", "Discord Inc.", "1.0.9", "Communication", "Voice, video, and team chat platform"),
        App("SlackTechnologies.Slack", "Slack", "Salesforce", "4.41", "Communication", "Team messaging and workspace collaboration"),
        App("Zoom.Zoom", "Zoom Workplace", "Zoom Video Communications", "6.2", "Communication", "HD video conferencing and screen sharing"),
        App("Tailscale.Tailscale", "Tailscale", "Tailscale Inc.", "1.76", "Networking", "Zero-config WireGuard mesh VPN"),
        App("AntibodySoftware.WizTree", "WizTree", "Antibody Software", "4.23", "Utilities", "Ultra-fast disk space analyzer"),
    ];

    private static WingetAppItem App(string id, string name, string pub, string ver, string cat, string desc) => new()
    {
        Id = id,
        IsBuiltIn = true,
        Name = name,
        Publisher = pub,
        Version = ver,
        Category = cat,
        Description = desc,
    };
}
