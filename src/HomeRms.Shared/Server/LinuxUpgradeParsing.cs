using System.Text.RegularExpressions;
using HomeRms.Shared.Protocol;

namespace HomeRms.Shared.Server;

public enum LinuxPackageManager { None, Apt, Dnf, Yum, Zypper, Pacman }

public enum LinuxUpgradeStage { Refreshing, Upgrading }

/// <summary>One command of an Update All run (no shell: file + argument list + extra environment).</summary>
public sealed record LinuxUpgradeStep(LinuxUpgradeStage Stage, string FileName, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    public string Display => $"{FileName} {string.Join(' ', Arguments)}";
}

public enum LinuxLineKind
{
    /// <summary>The package will be upgraded (from the "will be upgraded" list / transaction table).</summary>
    Queued,
    Downloading,
    Installing,
    Installed,
    /// <summary>The manager reports there is nothing to upgrade.</summary>
    NothingToDo,
    /// <summary>The run moved from refreshing metadata to upgrading.</summary>
    StageUpgrading,
}

public readonly record struct LinuxLineEvent(LinuxLineKind Kind, string? Package = null);

/// <summary>
/// Pure, testable parts of the Linux agent's Update All: package-manager detection, the
/// non-interactive command plan, and output line → event parsing for apt, dnf, yum, zypper and pacman.
/// </summary>
public static partial class LinuxUpgradeParser
{
    /// <summary>Detection order: the first binary that is present wins.</summary>
    public static readonly IReadOnlyList<(LinuxPackageManager Manager, string Binary)> DetectionOrder =
    [
        (LinuxPackageManager.Apt, "apt-get"),
        (LinuxPackageManager.Dnf, "dnf"),
        (LinuxPackageManager.Yum, "yum"),
        (LinuxPackageManager.Zypper, "zypper"),
        (LinuxPackageManager.Pacman, "pacman"),
    ];

    public static LinuxPackageManager Detect(Func<string, bool> hasBinary)
    {
        foreach (var (manager, binary) in DetectionOrder)
            if (hasBinary(binary)) return manager;
        return LinuxPackageManager.None;
    }

    public static string BinaryOf(LinuxPackageManager m) =>
        DetectionOrder.FirstOrDefault(d => d.Manager == m).Binary ?? "";

    /// <summary>The commands for one run, in order. Every command is non-interactive.</summary>
    public static IReadOnlyList<LinuxUpgradeStep> Plan(LinuxPackageManager m)
    {
        var none = new Dictionary<string, string>();
        return m switch
        {
            LinuxPackageManager.Apt =>
            [
                new(LinuxUpgradeStage.Refreshing, "apt-get", ["update"], AptEnv),
                new(LinuxUpgradeStage.Upgrading, "apt-get",
                    ["-y", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold", "upgrade"], AptEnv),
            ],
            LinuxPackageManager.Dnf => [new(LinuxUpgradeStage.Refreshing, "dnf", ["-y", "upgrade", "--refresh"], none)],
            LinuxPackageManager.Yum => [new(LinuxUpgradeStage.Refreshing, "yum", ["-y", "update"], none)],
            LinuxPackageManager.Zypper =>
            [
                new(LinuxUpgradeStage.Refreshing, "zypper", ["--non-interactive", "refresh"], none),
                new(LinuxUpgradeStage.Upgrading, "zypper", ["--non-interactive", "update"], none),
            ],
            LinuxPackageManager.Pacman => [new(LinuxUpgradeStage.Refreshing, "pacman", ["-Syu", "--noconfirm"], none)],
            _ => [],
        };
    }

    private static readonly Dictionary<string, string> AptEnv = new()
    {
        ["DEBIAN_FRONTEND"] = "noninteractive",
        ["NEEDRESTART_MODE"] = "a",
        ["APT_LISTCHANGES_FRONTEND"] = "none",
    };

    // ---- apt ----
    [GeneratedRegex(@"^Unpacking\s+([^\s:]+)(?::\S+)?\s+\(")]
    private static partial Regex AptUnpackRegex();
    [GeneratedRegex(@"^Setting up\s+([^\s:]+)(?::\S+)?\s+\(")]
    private static partial Regex AptSetupRegex();
    [GeneratedRegex(@"^Get:\d+\s+\S+\s+\S+\s+\S+\s+(\S+)\s+\S+\s+\S+\s+\[")]
    private static partial Regex AptGetRegex();
    [GeneratedRegex(@"^(\d+) upgraded, \d+ newly installed")]
    private static partial Regex AptSummaryRegex();
    [GeneratedRegex(@"^\s*Upgrading:\s*(\d+),")]
    private static partial Regex AptSummaryNewRegex();

    // ---- dnf / yum ----
    [GeneratedRegex(@"^\s*(?:\[\s*\d+/\d+\]\s*)?(?:Upgrading|Updating)\s*:?\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex DnfUpgradingRegex();
    [GeneratedRegex(@"^\(\d+/\d+\):\s+(\S+\.rpm)")]
    private static partial Regex DnfDownloadRegex();
    [GeneratedRegex(@"^\s*Verifying\s*:\s+(\S+)")]
    private static partial Regex DnfVerifyRegex();
    [GeneratedRegex(@"^\s(\S+)\s+(x86_64|noarch|aarch64|i686|i386|armv7hl|ppc64le|s390x)\s+\S+\s+\S+\s+[\d.]+\s*[kMG]?\s*$")]
    private static partial Regex DnfTableRowRegex();

    // ---- zypper ----
    [GeneratedRegex(@"^\(\s*\d+/\d+\)\s+Installing:\s+(\S+)")]
    private static partial Regex ZypperInstallRegex();
    [GeneratedRegex(@"^Retrieving(?::| package)\s+(\S+)")]
    private static partial Regex ZypperRetrieveRegex();
    [GeneratedRegex(@"^The following (?:\d+ )?packages? (?:is|are) going to be upgraded:")]
    private static partial Regex ZypperListHeaderRegex();

    // ---- pacman ----
    [GeneratedRegex(@"^\(\s*\d+/\d+\)\s+(?:upgrading|installing)\s+(\S+)")]
    private static partial Regex PacmanUpgradeRegex();
    [GeneratedRegex(@"^upgrading\s+(\S+?)\.\.\.")]
    private static partial Regex PacmanOldUpgradeRegex();
    [GeneratedRegex(@"^\s*(\S+)-(?:x86_64|any|aarch64|i686|armv7h)\s+downloading")]
    private static partial Regex PacmanDownloadRegex();
    [GeneratedRegex(@"^Packages\s+\(\d+\)\s+(.+)$")]
    private static partial Regex PacmanListRegex();

    private static readonly HashSet<string> Arches = new(StringComparer.Ordinal)
        { "x86_64", "noarch", "aarch64", "i686", "i386", "i586", "armv7hl", "ppc64le", "s390x", "any", "armv7h" };

    [GeneratedRegex(@"-\d")]
    private static partial Regex DashDigitRegex();

    /// <summary>name-version-release[.arch]: has a "-digit" (excludes "Updating Subscription Management repositories.").</summary>
    public static bool LooksLikeNevra(string token) => DashDigitRegex().IsMatch(token);

    /// <summary>"openssl-libs-1:3.0.7-27.el9.x86_64" → "openssl-libs"; "1:openssl-libs-1.0.2k-26.el7_9.x86_64" → "openssl-libs".</summary>
    public static string NameFromNevra(string nevra)
    {
        var s = nevra.Trim().TrimEnd('.', ',');
        if (s.EndsWith(".rpm", StringComparison.Ordinal)) s = s[..^4];
        var colon = s.IndexOf(':');
        if (colon > 0 && s[..colon].All(char.IsDigit)) s = s[(colon + 1)..];   // yum's "epoch:name-..."
        var dot = s.LastIndexOf('.');
        if (dot > 0 && Arches.Contains(s[(dot + 1)..])) s = s[..dot];
        var dash = s.LastIndexOf('-');
        if (dash > 0 && Arches.Contains(s[(dash + 1)..])) s = s[..dash];       // pacman "name-ver-rel-arch"
        for (var i = 0; i < 2; i++)
        {
            dash = s.LastIndexOf('-');
            if (dash <= 0) break;
            s = s[..dash];
        }
        return s;
    }

    /// <summary>
    /// Parses one output line. <paramref name="inList"/> carries the "packages will be upgraded" list state
    /// between calls (apt / zypper print the names on the indented lines that follow a header).
    /// </summary>
    public static List<LinuxLineEvent> ParseLine(LinuxPackageManager m, string rawLine, ref bool inList)
    {
        var events = new List<LinuxLineEvent>();
        var line = rawLine.TrimEnd();
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            inList = false;
            return events;
        }

        if (inList)
        {
            if (char.IsWhiteSpace(line[0]))
            {
                foreach (var name in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    events.Add(new LinuxLineEvent(LinuxLineKind.Queued, m == LinuxPackageManager.Apt ? name.Split(':')[0] : name));
                return events;
            }
            inList = false;
        }

        switch (m)
        {
            case LinuxPackageManager.Apt:
            {
                if (trimmed is "The following packages will be upgraded:" or "Upgrading:")
                {
                    inList = true;
                    break;
                }
                Match x;
                if ((x = AptUnpackRegex().Match(trimmed)).Success) events.Add(new(LinuxLineKind.Installing, x.Groups[1].Value));
                else if ((x = AptSetupRegex().Match(trimmed)).Success) events.Add(new(LinuxLineKind.Installed, x.Groups[1].Value));
                else if ((x = AptGetRegex().Match(trimmed)).Success) events.Add(new(LinuxLineKind.Downloading, x.Groups[1].Value));
                else if (((x = AptSummaryRegex().Match(trimmed)).Success || (x = AptSummaryNewRegex().Match(trimmed)).Success) && x.Groups[1].Value == "0")
                    events.Add(new(LinuxLineKind.NothingToDo));
                break;
            }
            case LinuxPackageManager.Dnf:
            case LinuxPackageManager.Yum:
            {
                if (trimmed.StartsWith("Dependencies resolved", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Resolving Dependencies", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Transaction Summary", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(new(LinuxLineKind.StageUpgrading));
                    break;
                }
                if (trimmed is "Nothing to do." or "Nothing to do" || trimmed.StartsWith("No packages marked for update", StringComparison.Ordinal))
                {
                    events.Add(new(LinuxLineKind.StageUpgrading));
                    events.Add(new(LinuxLineKind.NothingToDo));
                    break;
                }
                Match x;
                if ((x = DnfTableRowRegex().Match(line)).Success) events.Add(new(LinuxLineKind.Queued, x.Groups[1].Value));
                else if ((x = DnfUpgradingRegex().Match(line)).Success && LooksLikeNevra(x.Groups[1].Value)) events.Add(new(LinuxLineKind.Installing, NameFromNevra(x.Groups[1].Value)));
                else if ((x = DnfVerifyRegex().Match(line)).Success) events.Add(new(LinuxLineKind.Installed, NameFromNevra(x.Groups[1].Value)));
                else if ((x = DnfDownloadRegex().Match(trimmed)).Success) events.Add(new(LinuxLineKind.Downloading, NameFromNevra(x.Groups[1].Value)));
                break;
            }
            case LinuxPackageManager.Zypper:
            {
                if (ZypperListHeaderRegex().IsMatch(trimmed))
                {
                    inList = true;
                    break;
                }
                if (trimmed is "Nothing to do." || trimmed.StartsWith("No updates found", StringComparison.Ordinal))
                {
                    events.Add(new(LinuxLineKind.NothingToDo));
                    break;
                }
                Match x;
                if ((x = ZypperInstallRegex().Match(trimmed)).Success)
                {
                    var name = NameFromNevra(x.Groups[1].Value);
                    events.Add(new(LinuxLineKind.Installing, name));
                    if (trimmed.EndsWith("[done]", StringComparison.Ordinal)) events.Add(new(LinuxLineKind.Installed, name));
                }
                else if ((x = ZypperRetrieveRegex().Match(trimmed)).Success && x.Groups[1].Value != "repository")
                {
                    events.Add(new(LinuxLineKind.Downloading, NameFromNevra(x.Groups[1].Value)));
                }
                break;
            }
            case LinuxPackageManager.Pacman:
            {
                if (trimmed.StartsWith(":: Starting full system upgrade", StringComparison.Ordinal))
                {
                    events.Add(new(LinuxLineKind.StageUpgrading));
                    break;
                }
                if (trimmed is "there is nothing to do")
                {
                    events.Add(new(LinuxLineKind.NothingToDo));
                    break;
                }
                Match x;
                if ((x = PacmanListRegex().Match(trimmed)).Success)
                {
                    foreach (var p in x.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        events.Add(new(LinuxLineKind.Queued, NameFromNevra(p)));
                }
                else if ((x = PacmanUpgradeRegex().Match(trimmed)).Success)
                {
                    events.Add(new(LinuxLineKind.Installing, x.Groups[1].Value));
                    if (trimmed.EndsWith("100%", StringComparison.Ordinal)) events.Add(new(LinuxLineKind.Installed, x.Groups[1].Value));
                }
                else if ((x = PacmanOldUpgradeRegex().Match(trimmed)).Success)
                {
                    events.Add(new(LinuxLineKind.Installing, x.Groups[1].Value));
                }
                else if ((x = PacmanDownloadRegex().Match(trimmed)).Success)
                {
                    events.Add(new(LinuxLineKind.Downloading, NameFromNevra(x.Groups[1].Value)));
                }
                break;
            }
        }
        return events;
    }
}

/// <summary>
/// Turns Linux package-manager output into <see cref="WingetProgress"/> messages: a "system" row for the
/// stages (Refreshing = Downloading, Upgrading = Installing, then Succeeded / Failed / Skipped) and one row
/// per package where the output names it. The final message has package_id "system" and done = true.
/// </summary>
public sealed class LinuxUpgradeTracker(LinuxPackageManager manager)
{
    public const string SystemId = "system";

    private readonly Dictionary<string, WingetPhase> _rows = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private bool _inList;
    private bool _nothingToDo;
    private LinuxUpgradeStage _stage = LinuxUpgradeStage.Refreshing;

    public LinuxUpgradeStage Stage => _stage;
    public IReadOnlyList<string> Packages => _order;
    public WingetPhase? PhaseOf(string package) => _rows.TryGetValue(package, out var p) ? p : null;

    public static WingetPhase PhaseFor(LinuxUpgradeStage stage) =>
        stage == LinuxUpgradeStage.Refreshing ? WingetPhase.Downloading : WingetPhase.Installing;

    public List<WingetProgress> BeginStep(LinuxUpgradeStep step)
    {
        _inList = false;
        var output = new List<WingetProgress>();
        _stage = step.Stage;
        output.Add(new WingetProgress
        {
            PackageId = SystemId, Phase = PhaseFor(step.Stage), Percent = -1,
            LogLine = $"{(step.Stage == LinuxUpgradeStage.Refreshing ? "Refreshing" : "Upgrading")}: {step.Display}",
        });
        return output;
    }

    public List<WingetProgress> Feed(string rawLine)
    {
        var output = new List<WingetProgress>();
        var line = rawLine.Length > 2000 ? rawLine[..2000] : rawLine;
        var events = LinuxUpgradeParser.ParseLine(manager, line, ref _inList);
        var logged = false;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case LinuxLineKind.StageUpgrading:
                    if (_stage != LinuxUpgradeStage.Upgrading)
                    {
                        _stage = LinuxUpgradeStage.Upgrading;
                        output.Add(new WingetProgress { PackageId = SystemId, Phase = WingetPhase.Installing, Percent = -1 });
                    }
                    break;
                case LinuxLineKind.NothingToDo:
                    _nothingToDo = true;
                    break;
                case LinuxLineKind.Queued:
                    if (e.Package is { Length: > 0 } q && _rows.TryAdd(q, WingetPhase.Queued))
                    {
                        _order.Add(q);
                        output.Add(new WingetProgress { PackageId = q, Phase = WingetPhase.Queued, Percent = -1 });
                    }
                    break;
                default:
                    if (e.Package is not { Length: > 0 } p) break;
                    if (_stage != LinuxUpgradeStage.Upgrading)
                    {
                        _stage = LinuxUpgradeStage.Upgrading;
                        output.Add(new WingetProgress { PackageId = SystemId, Phase = WingetPhase.Installing, Percent = -1 });
                    }
                    var phase = e.Kind switch
                    {
                        LinuxLineKind.Downloading => WingetPhase.Downloading,
                        LinuxLineKind.Installing => WingetPhase.Installing,
                        _ => WingetPhase.Succeeded,
                    };
                    if (_rows.TryGetValue(p, out var cur) && (cur == WingetPhase.Succeeded || cur == phase))
                        break;
                    if (!_rows.ContainsKey(p)) _order.Add(p);
                    _rows[p] = phase;
                    output.Add(new WingetProgress
                    {
                        PackageId = p, Phase = phase, Percent = phase == WingetPhase.Succeeded ? 100 : -1,
                        LogLine = logged ? "" : line.Trim(),
                        ExitMessage = phase == WingetPhase.Succeeded ? "Upgraded" : "",
                    });
                    logged = true;
                    break;
            }
        }
        if (!logged && line.Trim().Length > 0)
            output.Add(new WingetProgress
            {
                PackageId = SystemId, Phase = _stage == LinuxUpgradeStage.Refreshing ? WingetPhase.Downloading : WingetPhase.Installing,
                Percent = -1, LogLine = line.Trim(),
            });
        return output;
    }

    /// <summary>Settles open rows and appends the final "system" message (done = true).</summary>
    public List<WingetProgress> Complete(int exitCode, string? failure = null)
    {
        var output = new List<WingetProgress>();
        var ok = exitCode == 0 && failure is null;
        var bin = LinuxUpgradeParser.BinaryOf(manager);
        int upgraded = 0, failed = 0, skipped = 0;
        foreach (var p in _order)
        {
            var cur = _rows[p];
            if (cur == WingetPhase.Succeeded) { upgraded++; continue; }
            var phase = ok
                ? cur == WingetPhase.Installing ? WingetPhase.Succeeded : WingetPhase.Skipped
                : WingetPhase.Failed;
            _rows[p] = phase;
            switch (phase)
            {
                case WingetPhase.Succeeded: upgraded++; break;
                case WingetPhase.Skipped: skipped++; break;
                default: failed++; break;
            }
            output.Add(new WingetProgress
            {
                PackageId = p, Phase = phase, Percent = phase == WingetPhase.Failed ? -1 : 100, ExitCode = ok ? 0 : exitCode,
                ExitMessage = phase switch
                {
                    WingetPhase.Succeeded => "Upgraded",
                    WingetPhase.Skipped => "No change reported",
                    _ => failure ?? $"Not upgraded ({bin} exited with code {exitCode})",
                },
            });
        }

        var upToDate = ok && upgraded == 0 && (_nothingToDo || _order.Count == 0);
        output.Add(new WingetProgress
        {
            PackageId = LinuxUpgradeTracker.SystemId,
            Phase = upToDate ? WingetPhase.Skipped : ok ? WingetPhase.Succeeded : WingetPhase.Failed,
            Percent = ok ? 100 : -1,
            ExitCode = exitCode,
            ExitMessage = upToDate ? "Everything is already up to date"
                : ok ? $"Success ({upgraded} upgraded)"
                : failure ?? $"{bin} exited with code {exitCode} ({upgraded} upgraded before the failure)",
            Done = true,
        });
        return output;
    }
}
