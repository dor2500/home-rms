using System.Globalization;
using System.Text.RegularExpressions;
using HomeRms.Shared.Protocol;

namespace HomeRms.Shared.Server;

/// <summary>One row of the <c>winget upgrade</c> table (an available upgrade).</summary>
public sealed record UpgradeCandidate(string Name, string Id, string Installed, string Available, string Source)
{
    /// <summary>Stable row key: the package ID as winget printed it (it may end in "…" when truncated).</summary>
    public string Key => Id.Length > 0 ? Id : Name;

    /// <summary>True when <paramref name="foundId"/> / <paramref name="foundName"/> from a "Found X [Id]" line is this row.</summary>
    public bool Matches(string? foundId, string? foundName)
    {
        if (!string.IsNullOrEmpty(foundId))
        {
            if (string.Equals(Id, foundId, StringComparison.OrdinalIgnoreCase)) return true;
            if (Id.EndsWith('…') && foundId.StartsWith(Id[..^1], StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (!string.IsNullOrEmpty(foundName))
        {
            if (string.Equals(Name, foundName, StringComparison.OrdinalIgnoreCase)) return true;
            if (Name.EndsWith('…') && foundName.StartsWith(Name[..^1], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

public enum WingetLineKind
{
    Text,
    /// <summary>"(1/5) Found Git [Git.Git] Version 2.47.0" — the next package starts.</summary>
    Found,
    /// <summary>A progress-bar redraw ("██▒▒ 12.5 MB / 40 MB", "███ 45%").</summary>
    Progress,
    Downloading,
    Installing,
    Succeeded,
    Failed,
    Skipped,
    /// <summary>"No installed package found matching input criteria." / "No available upgrade found."</summary>
    NothingToUpgrade,
}

public readonly record struct WingetLineEvent(
    WingetLineKind Kind, int Percent = -1, string? Name = null, string? Id = null,
    int Index = 0, int Total = 0, string? Version = null, int? ExitCode = null, string? Detail = null);

/// <summary>
/// Pure parsing of <c>winget upgrade</c> / <c>winget upgrade --all</c> output (English winget). Used by the
/// Windows agent while streaming, by the Controller for WinRM output and by ControllerChecks.
/// </summary>
public static partial class WingetUpgradeParser
{
    [GeneratedRegex(@"(?<!\d)(\d{1,3})(?:[.,]\d+)?\s?%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"([\d.,]+)\s*(B|KB|MB|GB)\s*/\s*([\d.,]+)\s*(B|KB|MB|GB)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^\((\d+)/(\d+)\)\s+Found\s+(.+?)\s+\[([^\]\s]+)\](?:\s+Version\s+(\S+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FoundRegex();

    [GeneratedRegex(@"^\s*Name\s+Id\s+Version\s+Available(\s+Source)?\s*$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^-{10,}\s*$")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"exit code:?\s*(0x[0-9A-Fa-f]+|-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerExitRegex();

    /// <summary>Splits on CR and LF (winget redraws progress bars with CR) and drops empty / whitespace-only pieces.</summary>
    public static IEnumerable<string> SplitLines(string text)
    {
        foreach (var piece in text.Split(['\r', '\n']))
            if (piece.Trim().Length > 0) yield return piece;
    }

    /// <summary>The "- \ | /" spinner winget draws while it works.</summary>
    public static bool IsSpinner(string line) => line.Trim() is "-" or "\\" or "|" or "/";

    /// <summary>
    /// Parses the table printed by a plain <c>winget upgrade</c>. Column boundaries come from the header
    /// line, so names with spaces and truncated ("…") cells are handled. Rows of the trailing "require
    /// explicit targeting" table (pinned packages that <c>--all</c> skips) are not returned.
    /// </summary>
    public static List<UpgradeCandidate> ParseUpgradeList(string output)
    {
        var result = new List<UpgradeCandidate>();
        var lines = output.Replace("\r\n", "\n").Split(['\r', '\n']); // a lone CR = spinner / progress redraw
        int[]? cols = null;  // Name, Id, Version, Available, Source (-1 when there is no Source column)
        var inRows = false;
        var explicitSection = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0 || IsSpinner(line))
            {
                if (inRows && line.Length == 0) inRows = false;
                continue;
            }
            if (line.Contains("require explicit targeting", StringComparison.OrdinalIgnoreCase))
            {
                explicitSection = true;
                inRows = false;
                continue;
            }

            var header = HeaderRegex().Match(line);
            if (header.Success)
            {
                var nameAt = line.IndexOf("Name", StringComparison.Ordinal);
                var src = line.IndexOf("Source", StringComparison.Ordinal);
                cols =
                [
                    0,
                    IndexOfWord(line, "Id") - nameAt,
                    IndexOfWord(line, "Version") - nameAt,
                    IndexOfWord(line, "Available") - nameAt,
                    src < 0 ? -1 : src - nameAt,
                ];
                inRows = false;
                continue;
            }
            if (cols is not null && SeparatorRegex().IsMatch(line.Trim()))
            {
                inRows = true;
                continue;
            }
            if (!inRows || cols is null) continue;
            if (char.IsDigit(line.TrimStart()[0]) && line.Contains("upgrade", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("available", StringComparison.OrdinalIgnoreCase) && line.Length < cols[3])
            {
                inRows = false; // "3 upgrades available."
                continue;
            }
            if (line.Length <= cols[3]) { inRows = false; continue; }
            if (explicitSection) continue;

            string Cell(int from, int to) =>
                from >= line.Length ? "" : line[from..Math.Min(to < 0 ? line.Length : to, line.Length)].Trim();

            var name = Cell(cols[0], cols[1]);
            var id = Cell(cols[1], cols[2]);
            var installed = Cell(cols[2], cols[3]);
            var available = Cell(cols[3], cols[4]);
            var source = cols[4] < 0 ? "" : Cell(cols[4], -1);
            if (id.Length == 0 || id.Contains(' ')) continue;
            result.Add(new UpgradeCandidate(name, id, installed, available, source));
        }
        return result;
    }

    private static int IndexOfWord(string line, string word)
    {
        var m = Regex.Match(line, $@"(?<=\s){Regex.Escape(word)}(?=\s|$)");
        return m.Success ? m.Index : line.IndexOf(word, StringComparison.Ordinal);
    }

    /// <summary>Classifies one trimmed line of <c>winget upgrade --all</c> output.</summary>
    public static WingetLineEvent Classify(string line)
    {
        line = line.Trim();
        var found = FoundRegex().Match(line);
        if (found.Success)
            return new WingetLineEvent(WingetLineKind.Found, Name: found.Groups[3].Value, Id: found.Groups[4].Value,
                Index: int.Parse(found.Groups[1].Value, CultureInfo.InvariantCulture),
                Total: int.Parse(found.Groups[2].Value, CultureInfo.InvariantCulture),
                Version: found.Groups[5].Success ? found.Groups[5].Value : null);

        var percent = ParsePercent(line);
        if (percent >= 0 && IsProgressBar(line)) return new WingetLineEvent(WingetLineKind.Progress, percent);

        if (line.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.Downloading);
        if (line.Contains("Starting package install", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Installing dependencies", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Starting package uninstall", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.Installing);

        if (line.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.Succeeded, ExitCode: 0, Detail: "Upgraded");
        if (line.Contains("Restart your PC to finish installation", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("restart is required to finish", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.Succeeded, ExitCode: unchecked((int)WingetRules.InstallRebootRequiredToFinish),
                Detail: WingetRules.Describe(unchecked((int)WingetRules.InstallRebootRequiredToFinish)));

        if (line.StartsWith("Installer failed with exit code", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Installation failed with exit code", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.Failed, ExitCode: ParseExitCode(line), Detail: line);
        if (line.Contains("Installer hash does not match", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.Failed, ExitCode: unchecked((int)WingetRules.InstallerHashMismatch), Detail: line);
        if (line.StartsWith("Installation abandoned", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Installation cancelled", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.Failed, ExitCode: unchecked((int)WingetRules.InstallCancelledByUser), Detail: line);
        if (line.Contains("install technology is different", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.Failed, Detail: "Install technology differs; uninstall it and install the newer version");

        if (line.Contains("cannot be upgraded using winget", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.Skipped, Detail: "Cannot be upgraded with winget (use the publisher's updater)");
        if (line.Contains("No applicable upgrade found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No available upgrade found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No newer package versions are available", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No installed package found matching input criteria", StringComparison.OrdinalIgnoreCase))
            return new WingetLineEvent(WingetLineKind.NothingToUpgrade);

        return new WingetLineEvent(WingetLineKind.Text, percent);
    }

    private static int? ParseExitCode(string line)
    {
        var m = InstallerExitRegex().Match(line);
        if (!m.Success) return null;
        var v = m.Groups[1].Value;
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u) ? unchecked((int)u) : null;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    public static int ParsePercent(string line)
    {
        var m = PercentRegex().Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p <= 100) return p;

        var s = SizeRegex().Match(line);
        if (s.Success &&
            TryBytes(s.Groups[1].Value, s.Groups[2].Value, out var done) &&
            TryBytes(s.Groups[3].Value, s.Groups[4].Value, out var total) && total > 0)
            return (int)Math.Clamp(done * 100 / total, 0, 100);
        return -1;
    }

    private static bool TryBytes(string number, string unit, out double bytes)
    {
        bytes = 0;
        if (!double.TryParse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
        bytes = unit.ToUpperInvariant() switch
        {
            "KB" => v * 1024,
            "MB" => v * 1024 * 1024,
            "GB" => v * 1024 * 1024 * 1024,
            _ => v,
        };
        return true;
    }

    /// <summary>winget progress bars are block characters followed by "x MB / y MB" or "n%".</summary>
    public static bool IsProgressBar(string line) =>
        line.Contains('█') || line.Contains('▒') || SizeRegex().IsMatch(line) ||
        PercentRegex().Match(line) is { Success: true } m && m.Index + m.Length >= line.Length - 1 && line.Length < 120;
}

/// <summary>
/// Turns <c>winget upgrade --all</c> output into per-package <see cref="WingetProgress"/> messages:
/// one Queued row per candidate, phase/percent updates, a terminal phase per package and a final
/// message (package_id "", done = true) carrying the overall exit code.
/// </summary>
public sealed class WingetUpgradeTracker
{
    private readonly List<UpgradeCandidate> _candidates;
    private readonly Dictionary<string, WingetPhase> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private string? _current;
    private WingetPhase _currentPhase;
    private int _lastPercent = int.MinValue;
    private bool _nothingToUpgrade;

    public WingetUpgradeTracker(IEnumerable<UpgradeCandidate>? candidates = null)
    {
        _candidates = candidates?.ToList() ?? [];
        foreach (var c in _candidates)
            if (_state.TryAdd(c.Key, WingetPhase.Queued)) _order.Add(c.Key);
    }

    public int Upgraded { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public IReadOnlyList<string> Packages => _order;
    public WingetPhase? PhaseOf(string key) => _state.TryGetValue(key, out var p) ? p : null;

    /// <summary>One Queued message per candidate (log_line describes the pending upgrade).</summary>
    public List<WingetProgress> QueuedRows() => _candidates
        .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
        .Select(c => new WingetProgress
        {
            PackageId = c.Key, Phase = WingetPhase.Queued, Percent = -1,
            LogLine = $"Queued: {c.Name} {c.Installed} -> {c.Available}".TrimEnd(),
        }).ToList();

    public List<WingetProgress> Feed(string rawLine)
    {
        var output = new List<WingetProgress>();
        var line = rawLine.Trim();
        if (line.Length == 0 || WingetUpgradeParser.IsSpinner(line)) return output;
        if (line.Length > 2000) line = line[..2000];

        var e = WingetUpgradeParser.Classify(line);
        switch (e.Kind)
        {
            case WingetLineKind.Found:
            {
                CloseCurrent(output, WingetPhase.Failed, null, "winget moved on without reporting a result for this package");
                var key = _candidates.FirstOrDefault(c => c.Matches(e.Id, e.Name))?.Key ?? e.Id ?? e.Name ?? "package";
                if (_state.TryAdd(key, WingetPhase.Queued)) _order.Add(key);
                _current = key;
                _currentPhase = WingetPhase.Queued;
                _lastPercent = int.MinValue;
                output.Add(Msg(key, WingetPhase.Queued, -1, line));
                break;
            }
            case WingetLineKind.Progress:
                if (_current is null || e.Percent == _lastPercent) break;
                _lastPercent = e.Percent;
                if (_currentPhase == WingetPhase.Queued) _currentPhase = WingetPhase.Downloading;
                _state[_current] = _currentPhase;
                output.Add(Msg(_current, _currentPhase, e.Percent, ""));
                break;
            case WingetLineKind.Downloading or WingetLineKind.Installing when _current is not null:
                _currentPhase = e.Kind == WingetLineKind.Downloading ? WingetPhase.Downloading : WingetPhase.Installing;
                _state[_current] = _currentPhase;
                _lastPercent = int.MinValue;
                output.Add(Msg(_current, _currentPhase, -1, line));
                break;
            case WingetLineKind.Succeeded or WingetLineKind.Failed or WingetLineKind.Skipped when _current is not null:
            {
                var phase = e.Kind switch
                {
                    WingetLineKind.Succeeded => WingetPhase.Succeeded,
                    WingetLineKind.Skipped => WingetPhase.Skipped,
                    _ => WingetPhase.Failed,
                };
                output.Add(Msg(_current, _currentPhase, -1, line));
                CloseCurrent(output, phase, e.ExitCode, e.Detail);
                break;
            }
            case WingetLineKind.NothingToUpgrade:
                _nothingToUpgrade = true;
                output.Add(Msg(_current ?? "", _current is null ? WingetPhase.Queued : _currentPhase, -1, line));
                break;
            default:
                output.Add(Msg(_current ?? "", _current is null ? WingetPhase.Queued : _currentPhase, -1, line));
                break;
        }
        return output;
    }

    /// <summary>Closes any open package, settles rows winget never reached and appends the final (done) message.</summary>
    public List<WingetProgress> Complete(int exitCode)
    {
        var output = new List<WingetProgress>();
        var ok = WingetRules.IsSuccess(exitCode);
        var overall = WingetRules.Describe(exitCode);
        CloseCurrent(output, ok ? WingetPhase.Succeeded : WingetPhase.Failed, ok ? 0 : exitCode, ok ? "Upgraded" : overall);

        foreach (var key in _order.Where(k => _state[k] is WingetPhase.Queued).ToList())
        {
            var phase = ok ? WingetPhase.Skipped : WingetPhase.Failed;
            _state[key] = phase;
            if (ok) Skipped++; else Failed++;
            output.Add(new WingetProgress
            {
                PackageId = key, Phase = phase, Percent = -1, ExitCode = ok ? 0 : exitCode,
                ExitMessage = ok ? "Not upgraded by winget (no applicable upgrade)" : $"Not upgraded: {overall}",
            });
        }

        var code = unchecked((uint)exitCode);
        var upToDate = Upgraded == 0 && Failed == 0 &&
                       (_nothingToUpgrade || code is WingetRules.UpdateNotApplicable or WingetRules.NoPackageFound || (ok && _order.Count == 0));
        var finalPhase = upToDate ? WingetPhase.Skipped : ok && Failed == 0 ? WingetPhase.Succeeded : WingetPhase.Failed;
        var summary = upToDate
            ? "Everything is already up to date"
            : $"{overall} ({Upgraded} upgraded, {Failed} failed, {Skipped} skipped)";
        output.Add(new WingetProgress
        {
            PackageId = "", Phase = finalPhase, Percent = finalPhase == WingetPhase.Failed ? -1 : 100,
            ExitCode = upToDate ? 0 : exitCode, ExitMessage = summary, Done = true,
        });
        return output;
    }

    private void CloseCurrent(List<WingetProgress> output, WingetPhase phase, int? exitCode, string? detail)
    {
        if (_current is null) return;
        var key = _current;
        _current = null;
        _state[key] = phase;
        switch (phase)
        {
            case WingetPhase.Succeeded: Upgraded++; break;
            case WingetPhase.Skipped: Skipped++; break;
            default: Failed++; break;
        }
        var code = exitCode ?? (phase == WingetPhase.Succeeded || phase == WingetPhase.Skipped ? 0 : unchecked((int)WingetRules.UpdateAllHasFailure));
        var message = string.IsNullOrWhiteSpace(detail) ? WingetRules.Describe(code) : detail;
        output.Add(new WingetProgress
        {
            PackageId = key, Phase = phase, Percent = phase == WingetPhase.Failed ? -1 : 100,
            ExitCode = code, ExitMessage = message,
        });
    }

    private static WingetProgress Msg(string id, WingetPhase phase, int percent, string line) =>
        new() { PackageId = id, Phase = phase, Percent = percent, LogLine = line };
}
