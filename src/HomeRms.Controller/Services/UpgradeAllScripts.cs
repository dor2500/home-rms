using System;
using System.Collections.Generic;
using System.Linq;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

namespace HomeRms.Controller.Services;

/// <summary>
/// Update All for agentless (WinRM) endpoints: the PowerShell that lists the available upgrades, prints
/// <see cref="ListEndMarker"/>, runs <c>winget upgrade --all</c> and prints the exit-code marker, plus the
/// parser that turns the collected output into the same <see cref="WingetProgress"/> messages an agent streams.
/// </summary>
public static class UpgradeAllScripts
{
    public const string ListEndMarker = "HOMERMS_UPGRADE_LIST_END";
    public const string WingetMissingText = "winget.exe was not found on this computer (App Installer is missing or not registered).";

    public static string BuildWingetUpgradeAllScript(bool includeUnknown, bool silent)
    {
        var unknown = includeUnknown ? " --include-unknown" : "";
        var quiet = silent ? " --silent" : "";
        return $$"""
            $ErrorActionPreference = 'Continue'
            $ProgressPreference = 'SilentlyContinue'
            $winget = Get-Command winget.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
            if (-not $winget) {
                $winget = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe" -ErrorAction SilentlyContinue |
                    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
            }
            if (-not $winget) {
                Write-Output '{{WingetMissingText}}'
                Write-Output '{{ListEndMarker}}'
                Write-Output "{{WingetCatalogService.ExitMarker}}{{unchecked((int)WingetRules.CommandFailed)}}"
                exit 1
            }
            & $winget upgrade --accept-source-agreements --disable-interactivity{{unknown}}
            Write-Output '{{ListEndMarker}}'
            & $winget upgrade --all{{quiet}} --accept-package-agreements --accept-source-agreements --disable-interactivity{{unknown}}
            $code = $LASTEXITCODE
            Write-Output "{{WingetCatalogService.ExitMarker}}$code"
            exit $code
            """;
    }

    /// <summary>
    /// Replays WinRM output through <see cref="WingetUpgradeTracker"/>. Returns the messages (Queued rows,
    /// per-package phases, log lines, the final done message) and the winget exit code, or null when the
    /// exit-code marker is missing (the WinRM run itself failed; no messages are returned then).
    /// </summary>
    public static (List<WingetProgress> Messages, int? ExitCode) ParseWinRmOutput(string stdout)
    {
        var code = WingetCatalogService.ParseExitMarker(stdout);
        if (code is null) return ([], null);

        var split = stdout.IndexOf(ListEndMarker, StringComparison.Ordinal);
        var listPart = split < 0 ? "" : stdout[..split];
        var runPart = split < 0 ? stdout : stdout[(split + ListEndMarker.Length)..];

        var messages = new List<WingetProgress>();
        foreach (var line in WingetUpgradeParser.SplitLines(listPart))
        {
            var t = line.Trim();
            if (WingetUpgradeParser.IsSpinner(t) || WingetUpgradeParser.ParsePercent(t) >= 0 && WingetUpgradeParser.IsProgressBar(t)) continue;
            messages.Add(new WingetProgress { PackageId = "", Phase = WingetPhase.Queued, Percent = -1, LogLine = t });
        }

        var tracker = new WingetUpgradeTracker(WingetUpgradeParser.ParseUpgradeList(listPart));
        messages.AddRange(tracker.QueuedRows());
        foreach (var line in WingetUpgradeParser.SplitLines(runPart))
        {
            if (line.Contains(WingetCatalogService.ExitMarker, StringComparison.Ordinal)) continue;
            messages.AddRange(tracker.Feed(line));
        }
        messages.AddRange(tracker.Complete(code.Value));
        return (messages, code);
    }
}
