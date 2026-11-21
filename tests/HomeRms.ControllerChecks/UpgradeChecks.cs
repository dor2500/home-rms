using System.Text;
using HomeRms.Controller.Services;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

/// <summary>
/// Update All parsing on realistic output samples: the winget upgrade table, winget upgrade --all per-package
/// lines, the WinRM replay, and the Linux managers (detection, command plan, line parsing, tracker).
/// </summary>
internal static class UpgradeChecks
{
    public static void Run(Action<bool, string> check)
    {
        WingetList(check);
        WingetUpgradeAll(check);
        WingetEdgeCases(check);
        WinRmReplay(check);
        LinuxDetection(check);
        LinuxApt(check);
        LinuxDnfYum(check);
        LinuxZypper(check);
        LinuxPacman(check);
        NevraNames(check);
    }

    // ---------------- winget ----------------
    // winget pads every column to a fixed width and truncates long cells with "…" (as printed on a 120-column console).
    private static string Row(string name, string id, string version, string available, string source) =>
        name.PadRight(39) + id.PadRight(34) + version.PadRight(17) + available.PadRight(17) + source;

    private static string WingetListSample() => string.Join("\r\n",
    [
        "   - \r   \\ \r   | \r                                                                                                                        \r" +
        Row("Name", "Id", "Version", "Available", "Source"),
        new string('-', 112),
        Row("Git", "Git.Git", "2.46.0", "2.47.1", "winget"),
        Row("Microsoft Edge", "Microsoft.Edge", "130.0.2849.80", "131.0.2903.70", "winget"),
        Row("7-Zip 23.01 (x64)", "7zip.7zip", "23.01", "24.09", "winget"),
        Row("Microsoft Visual Studio Code (User)", "Microsoft.VisualStudioCode", "1.94.2", "1.95.3", "winget"),
        Row("Microsoft Visual C++ 2015-2022 Redist…", "Microsoft.VCRedist.2015+.x64", "14.40.33810.0", "14.42.34433.0", "winget"),
        Row("Visual Studio Build Tools 2022", "Microsoft.VisualStudio.2022.Buil…", "17.11.5", "17.12.1", "winget"),
        "6 upgrades available.",
        "",
        "The following packages have an upgrade available, but require explicit targeting for upgrade:",
        "Name    Id              Version Available Source",
        "------------------------------------------------",
        "Discord Discord.Discord 1.0.9   1.0.9170  winget",
        "",
        "1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.",
    ]);

    private static readonly string[] UpgradeAllSample =
    [
        "(1/6) Found Git [Git.Git] Version 2.47.1",
        "This application is licensed to you by its owner.",
        "Microsoft is not responsible for, nor does it grant any licenses to, third-party packages.",
        "Downloading https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.1/Git-2.47.1-64-bit.exe",
        "  ██████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  25.0 MB / 64.9 MB\r  ██████████████████████████████  64.9 MB / 64.9 MB",
        "Successfully verified installer hash",
        "Starting package install...",
        "Successfully installed",
        "(2/6) Found Microsoft Edge [Microsoft.Edge] Version 131.0.2903.70",
        "This application is licensed to you by its owner.",
        "Downloading https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/MicrosoftEdgeEnterpriseX64.msi",
        "  ██████████████████████████████   161 MB /  161 MB",
        "Successfully verified installer hash",
        "Starting package install...",
        "Installer failed with exit code: 1603",
        "(3/6) Found 7-Zip [7zip.7zip] Version 24.09",
        "Downloading https://7-zip.org/a/7z2409-x64.msi",
        "  ██████████████████████████████  1.88 MB / 1.88 MB",
        "Successfully verified installer hash",
        "Starting package install...",
        "  ███████████████               50%\r  ██████████████████████████████  100%",
        "Successfully installed",
        "(4/6) Found Microsoft Visual Studio Code (User) [Microsoft.VisualStudioCode] Version 1.95.3",
        "Downloading https://vscode.download.prss.microsoft.com/dbazure/download/stable/VSCodeUserSetup-x64-1.95.3.exe",
        "  ██████████████████████████████   102 MB /  102 MB",
        "Starting package install...",
        "Successfully installed",
        "(5/6) Found Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.42.34433 [Microsoft.VCRedist.2015+.x64] Version 14.42.34433.0",
        "Downloading https://download.visualstudio.microsoft.com/download/pr/VC_redist.x64.exe",
        "  ██████████████████████████████  24.4 MB / 24.4 MB",
        "Starting package install...",
        "Restart your PC to finish installation.",
        "(6/6) Found Visual Studio Build Tools 2022 [Microsoft.VisualStudio.2022.BuildTools] Version 17.12.1",
        "A newer version was found, but the install technology is different from the current version installed. Please uninstall the package and install the newer version.",
    ];

    private static void WingetList(Action<bool, string> check)
    {
        var list = WingetUpgradeParser.ParseUpgradeList(WingetListSample());
        check(list.Count == 6, $"winget list: 6 upgradable packages parsed (got {list.Count}: {string.Join(", ", list.Select(c => c.Id))})");
        check(list.Count > 0 && list[0] is { Name: "Git", Id: "Git.Git", Installed: "2.46.0", Available: "2.47.1", Source: "winget" },
            "winget list: columns split at the header positions");
        check(list.Any(c => c.Name == "7-Zip 23.01 (x64)" && c.Id == "7zip.7zip" && c.Available == "24.09"), "winget list: names with spaces/parentheses");
        check(list.Any(c => c.Id == "Microsoft.VisualStudio.2022.Buil…"), "winget list: a truncated Id is kept as printed");
        check(list.All(c => c.Id != "Discord.Discord"), "winget list: 'require explicit targeting' (pinned) packages are excluded");

        var none = WingetUpgradeParser.ParseUpgradeList("   - \r   \\ \rNo installed package found matching input criteria.\r\n");
        check(none.Count == 0, "winget list: 'No installed package found' -> empty list");
    }

    private static (List<WingetProgress> Messages, WingetUpgradeTracker Tracker) Replay(IEnumerable<string> lines, int exitCode, IEnumerable<UpgradeCandidate>? candidates)
    {
        var tracker = new WingetUpgradeTracker(candidates);
        var messages = tracker.QueuedRows();
        foreach (var chunk in lines)
            foreach (var line in WingetUpgradeParser.SplitLines(chunk))
                messages.AddRange(tracker.Feed(line));
        messages.AddRange(tracker.Complete(exitCode));
        return (messages, tracker);
    }

    private static void WingetUpgradeAll(Action<bool, string> check)
    {
        var candidates = WingetUpgradeParser.ParseUpgradeList(WingetListSample());
        var (messages, tracker) = Replay(UpgradeAllSample, unchecked((int)WingetRules.UpdateAllHasFailure), candidates);

        var queued = messages.TakeWhile(m => m.Phase == WingetPhase.Queued && m.LogLine.StartsWith("Queued:", StringComparison.Ordinal)).Count();
        check(queued == 6, $"winget --all: one Queued row per listed package first (got {queued})");
        check(tracker.PhaseOf("Git.Git") == WingetPhase.Succeeded, "winget --all: 'Successfully installed' -> Succeeded");
        check(tracker.PhaseOf("Microsoft.Edge") == WingetPhase.Failed, "winget --all: 'Installer failed with exit code' -> Failed");
        var edge = messages.LastOrDefault(m => m.PackageId == "Microsoft.Edge" && m.Phase == WingetPhase.Failed);
        check(edge?.ExitCode == 1603, $"winget --all: installer exit code 1603 is carried (got {edge?.ExitCode})");
        check(tracker.PhaseOf("Microsoft.VCRedist.2015+.x64") == WingetPhase.Succeeded &&
              messages.Any(m => m.PackageId == "Microsoft.VCRedist.2015+.x64" && m.ExitCode == unchecked((int)WingetRules.InstallRebootRequiredToFinish)),
            "winget --all: 'Restart your PC to finish installation' -> Succeeded with the reboot-required code");
        check(tracker.PhaseOf("Microsoft.VisualStudio.2022.Buil…") == WingetPhase.Failed,
            "winget --all: a 'Found' line matches the truncated Id row; install-technology mismatch -> Failed");
        check(tracker.Packages.Count == 6, $"winget --all: no extra rows for matched packages (rows: {string.Join(", ", tracker.Packages)})");

        var gitPercents = messages.Where(m => m.PackageId == "Git.Git" && m.Percent >= 0 && m.LogLine.Length == 0 && m.Phase is WingetPhase.Downloading or WingetPhase.Installing).Select(m => m.Percent).ToList();
        check(gitPercents.SequenceEqual([38, 100]), $"winget --all: progress bars -> percent updates without log lines (got {string.Join(",", gitPercents)})");
        check(messages.Any(m => m.PackageId == "Git.Git" && m.Phase == WingetPhase.Downloading) &&
              messages.Any(m => m.PackageId == "Git.Git" && m.Phase == WingetPhase.Installing), "winget --all: Downloading -> Installing phases");
        check(messages.Any(m => m.PackageId == "Git.Git" && m.LogLine == "Successfully verified installer hash"), "winget --all: raw lines streamed as log_line");

        var final = messages[^1];
        check(final.Done && final.PackageId == "" && final.Phase == WingetPhase.Failed && final.ExitCode == unchecked((int)WingetRules.UpdateAllHasFailure),
            "winget --all: final message is done, package '' and carries the overall exit code");
        check(final.ExitMessage.Contains("One or more upgrades failed") && final.ExitMessage.Contains("4 upgraded, 2 failed"),
            $"winget --all: final message uses the exit-code table and counts ('{final.ExitMessage}')");
        check(messages.Count(m => m.Done) == 1, "winget --all: exactly one done message");
    }

    private static void WingetEdgeCases(Action<bool, string> check)
    {
        // nothing to upgrade
        var (none, _) = Replay(["No installed package found matching input criteria."], unchecked((int)WingetRules.NoPackageFound), null);
        check(none[^1] is { Done: true, Phase: WingetPhase.Skipped, ExitCode: 0 } && none[^1].ExitMessage.Contains("up to date"),
            "winget --all: nothing to upgrade -> Skipped 'up to date'");

        // cancelled while downloading: the open package and the unreached ones fail, nothing succeeds
        var cands = new[] { new UpgradeCandidate("Git", "Git.Git", "1", "2", "winget"), new UpgradeCandidate("7-Zip", "7zip.7zip", "1", "2", "winget") };
        var (cancel, t2) = Replay(["(1/2) Found Git [Git.Git] Version 2", "Downloading https://example.invalid/git.exe", "  ███▒▒▒▒▒▒▒  3 MB / 60 MB"],
            unchecked((int)WingetRules.CtrlSignalReceived), cands);
        check(t2.PhaseOf("Git.Git") == WingetPhase.Failed && t2.PhaseOf("7zip.7zip") == WingetPhase.Failed && cancel[^1].Phase == WingetPhase.Failed,
            "winget --all: Ctrl+C exit -> open and unreached packages Failed");
        check(cancel[^1].ExitMessage.Contains("Cancelled"), "winget --all: cancel exit code is described");

        // all succeed, a listed package winget never reached -> Skipped
        var (ok, t3) = Replay(["(1/1) Found Git [Git.Git] Version 2", "Successfully installed"], 0, cands);
        check(ok[^1].Phase == WingetPhase.Succeeded && t3.PhaseOf("7zip.7zip") == WingetPhase.Skipped, "winget --all: exit 0, unreached row -> Skipped");

        // unknown package (not in the list) gets its own row
        var (extra, t4) = Replay(["(1/1) Found Some App [Vendor.SomeApp] Version 3.0", "Successfully installed"], 0, null);
        check(t4.PhaseOf("Vendor.SomeApp") == WingetPhase.Succeeded && extra.Any(m => m.PackageId == "Vendor.SomeApp" && m.Phase == WingetPhase.Queued),
            "winget --all: a package found during the run but not listed gets a row");

        var hex = WingetUpgradeParser.Classify("Installer failed with exit code: 0x80070005 : Access is denied.");
        check(hex.Kind == WingetLineKind.Failed && hex.ExitCode == unchecked((int)0x80070005), "winget: hex installer exit codes are parsed");
        check(WingetUpgradeParser.Classify("Successfully verified installer hash").Kind == WingetLineKind.Text, "winget: 'verified installer hash' is not a result");
    }

    private static void WinRmReplay(Action<bool, string> check)
    {
        var stdout = new StringBuilder()
            .Append(WingetListSample()).Append("\r\n")
            .Append(UpgradeAllScripts.ListEndMarker).Append("\r\n")
            .Append(string.Join("\r\n", UpgradeAllSample)).Append("\r\n")
            .Append(WingetCatalogService.ExitMarker).Append(unchecked((int)WingetRules.UpdateAllHasFailure)).Append("\r\n")
            .ToString();
        var (messages, code) = UpgradeAllScripts.ParseWinRmOutput(stdout);
        check(code == unchecked((int)WingetRules.UpdateAllHasFailure), "WinRM Update All: exit-code marker parsed");
        check(messages.Count(m => m.Phase == WingetPhase.Queued && m.LogLine.StartsWith("Queued:", StringComparison.Ordinal)) == 6,
            "WinRM Update All: list part -> 6 Queued rows");
        check(messages.Any(m => m.PackageId == "Git.Git" && m.Phase == WingetPhase.Succeeded) && messages[^1].Done,
            "WinRM Update All: run part -> per-package results and a done message");
        check(messages.All(m => !m.LogLine.Contains(WingetCatalogService.ExitMarker) && !m.LogLine.Contains(UpgradeAllScripts.ListEndMarker)),
            "WinRM Update All: markers are not logged");

        var (noMarker, noCode) = UpgradeAllScripts.ParseWinRmOutput("Connecting to remote server pc failed");
        check(noCode is null && noMarker.Count == 0, "WinRM Update All: missing marker -> null exit code (WinRM failure)");
    }

    // ---------------- Linux ----------------
    private static void LinuxDetection(Action<bool, string> check)
    {
        LinuxPackageManager Detect(params string[] present) => LinuxUpgradeParser.Detect(b => present.Contains(b));
        check(Detect("apt-get") == LinuxPackageManager.Apt, "detect: apt-get");
        check(Detect("dnf", "yum") == LinuxPackageManager.Dnf, "detect: dnf preferred over yum (yum is often a dnf alias)");
        check(Detect("yum") == LinuxPackageManager.Yum, "detect: yum");
        check(Detect("zypper") == LinuxPackageManager.Zypper, "detect: zypper");
        check(Detect("pacman") == LinuxPackageManager.Pacman, "detect: pacman");
        check(Detect("apt-get", "pacman", "dnf") == LinuxPackageManager.Apt, "detect: order apt-get, dnf, yum, zypper, pacman");
        check(Detect("apk", "emerge") == LinuxPackageManager.None, "detect: unsupported managers -> None");

        var apt = LinuxUpgradeParser.Plan(LinuxPackageManager.Apt);
        check(apt.Count == 2 && apt[0].Arguments.SequenceEqual(["update"]) && apt[1].Arguments[^1] == "upgrade" &&
              apt[1].Arguments.Contains("-y") && apt[1].Arguments.Contains("Dpkg::Options::=--force-confold") &&
              apt.All(s => s.Environment.TryGetValue("DEBIAN_FRONTEND", out var f) && f == "noninteractive"),
            "plan: apt-get update, then non-interactive apt-get -y ... upgrade (not dist-upgrade)");
        check(LinuxUpgradeParser.Plan(LinuxPackageManager.Dnf).Single().Display == "dnf -y upgrade --refresh", "plan: dnf -y upgrade --refresh");
        check(LinuxUpgradeParser.Plan(LinuxPackageManager.Yum).Single().Display == "yum -y update", "plan: yum -y update");
        var zy = LinuxUpgradeParser.Plan(LinuxPackageManager.Zypper);
        check(zy.Count == 2 && zy[0].Display == "zypper --non-interactive refresh" && zy[1].Display == "zypper --non-interactive update",
            "plan: zypper refresh, then update (non-interactive)");
        check(LinuxUpgradeParser.Plan(LinuxPackageManager.Pacman).Single().Display == "pacman -Syu --noconfirm", "plan: pacman -Syu --noconfirm");
        check(LinuxUpgradeParser.Plan(LinuxPackageManager.None).Count == 0, "plan: none for an unsupported system");
    }

    private static (List<WingetProgress> Messages, LinuxUpgradeTracker Tracker) LinuxRun(LinuxPackageManager m, string[][] stepOutputs, int exitCode = 0, string? failure = null)
    {
        var tracker = new LinuxUpgradeTracker(m);
        var messages = new List<WingetProgress>();
        var plan = LinuxUpgradeParser.Plan(m);
        for (var i = 0; i < stepOutputs.Length; i++)
        {
            messages.AddRange(tracker.BeginStep(plan[Math.Min(i, plan.Count - 1)]));
            foreach (var line in stepOutputs[i]) messages.AddRange(tracker.Feed(line));
        }
        messages.AddRange(tracker.Complete(exitCode, failure));
        return (messages, tracker);
    }

    private static void LinuxApt(Action<bool, string> check)
    {
        string[] update =
        [
            "Hit:1 http://archive.ubuntu.com/ubuntu noble InRelease",
            "Get:2 http://archive.ubuntu.com/ubuntu noble-updates InRelease [126 kB]",
            "Get:5 http://archive.ubuntu.com/ubuntu noble-updates/main amd64 Packages [612 kB]",
            "Get:6 http://archive.ubuntu.com/ubuntu noble-updates/main Translation-en [151 kB]",
            "Fetched 889 kB in 1s (761 kB/s)",
            "Reading package lists...",
        ];
        string[] upgrade =
        [
            "Reading package lists...",
            "Building dependency tree...",
            "Reading state information...",
            "Calculating upgrade...",
            "The following packages will be upgraded:",
            "  libssl3t64 openssl tzdata",
            "3 upgraded, 0 newly installed, 0 to remove and 0 not upgraded.",
            "Need to get 3,215 kB of archives.",
            "Get:1 http://archive.ubuntu.com/ubuntu noble-updates/main amd64 libssl3t64 amd64 3.0.13-0ubuntu3.4 [1,940 kB]",
            "Get:2 http://archive.ubuntu.com/ubuntu noble-updates/main amd64 openssl amd64 3.0.13-0ubuntu3.4 [1,002 kB]",
            "Get:3 http://archive.ubuntu.com/ubuntu noble-updates/main amd64 tzdata all 2024a-3ubuntu1.1 [273 kB]",
            "Fetched 3,215 kB in 1s (2,100 kB/s)",
            "(Reading database ... 74312 files and directories currently installed.)",
            "Preparing to unpack .../libssl3t64_3.0.13-0ubuntu3.4_amd64.deb ...",
            "Unpacking libssl3t64:amd64 (3.0.13-0ubuntu3.4) over (3.0.13-0ubuntu3.1) ...",
            "Preparing to unpack .../openssl_3.0.13-0ubuntu3.4_amd64.deb ...",
            "Unpacking openssl (3.0.13-0ubuntu3.4) over (3.0.13-0ubuntu3.1) ...",
            "Unpacking tzdata (2024a-3ubuntu1.1) over (2024a-3ubuntu1) ...",
            "Setting up libssl3t64:amd64 (3.0.13-0ubuntu3.4) ...",
            "Setting up tzdata (2024a-3ubuntu1.1) ...",
            "Setting up openssl (3.0.13-0ubuntu3.4) ...",
            "Processing triggers for libc-bin (2.39-0ubuntu8.3) ...",
        ];
        var (messages, tracker) = LinuxRun(LinuxPackageManager.Apt, [update, upgrade]);
        check(tracker.Packages.SequenceEqual(["libssl3t64", "openssl", "tzdata"]), $"apt: rows from 'will be upgraded' (got {string.Join(", ", tracker.Packages)})");
        check(tracker.Packages.All(p => tracker.PhaseOf(p) == WingetPhase.Succeeded), "apt: 'Setting up' -> Succeeded for every package");
        check(messages.Any(m => m.PackageId == "openssl" && m.Phase == WingetPhase.Downloading) &&
              messages.Any(m => m.PackageId == "openssl" && m.Phase == WingetPhase.Installing), "apt: Get -> Downloading, Unpacking -> Installing");
        check(messages.Where(m => m.PackageId == LinuxUpgradeTracker.SystemId).Select(m => m.Phase).Distinct()
                  .SequenceEqual([WingetPhase.Downloading, WingetPhase.Installing, WingetPhase.Succeeded]),
            "apt: system row goes Refreshing -> Upgrading -> Succeeded");
        check(!messages.Any(m => m.PackageId is "Packages" or "Translation-en" or "libc-bin"), "apt: index downloads and triggers are not packages");
        check(messages[^1] is { Done: true, PackageId: LinuxUpgradeTracker.SystemId, Phase: WingetPhase.Succeeded } && messages[^1].ExitMessage.Contains("3 upgraded"),
            "apt: final done message on the system row");
        check(messages.Count(m => m.LogLine == "Processing triggers for libc-bin (2.39-0ubuntu8.3) ...") == 1, "apt: every raw line is logged once");

        var (nothing, _) = LinuxRun(LinuxPackageManager.Apt, [update, ["Calculating upgrade...", "0 upgraded, 0 newly installed, 0 to remove and 0 not upgraded."]]);
        check(nothing[^1].Phase == WingetPhase.Skipped && nothing[^1].ExitMessage.Contains("up to date"), "apt: '0 upgraded' -> up to date");

        string[] newApt = ["Upgrading:", "  libssl3t64  openssl", "", "Summary:", "  Upgrading: 2, Installing: 0, Removing: 0, Not Upgrading: 0",
                           "Setting up openssl (3.0.13-0ubuntu3.4) ..."];
        var (_, t2) = LinuxRun(LinuxPackageManager.Apt, [[], newApt]);
        check(t2.Packages.SequenceEqual(["libssl3t64", "openssl"]), "apt 3.x: 'Upgrading:' list format");

        var (failed, t3) = LinuxRun(LinuxPackageManager.Apt, [update, ["The following packages will be upgraded:", "  openssl",
            "E: Could not get lock /var/lib/dpkg/lock-frontend. It is held by process 1234 (unattended-upgr)"]], 100, "'apt-get ... upgrade' failed with exit code 100");
        check(failed[^1] is { Done: true, Phase: WingetPhase.Failed, ExitCode: 100 } && t3.PhaseOf("openssl") == WingetPhase.Failed,
            "apt: lock error exit 100 -> Failed system row and package rows");
    }

    private static void LinuxDnfYum(Action<bool, string> check)
    {
        string[] dnf =
        [
            "Last metadata expiration check: 0:00:01 ago on Sat 21 Nov 2026 10:00:00 AM UTC.",
            "Dependencies resolved.",
            "================================================================================",
            " Package             Arch       Version                   Repository      Size",
            "================================================================================",
            "Upgrading:",
            " openssl             x86_64     1:3.0.7-27.el9            baseos         1.2 M",
            " openssl-libs        x86_64     1:3.0.7-27.el9            baseos         2.1 M",
            " tzdata              noarch     2024b-2.el9               baseos         841 k",
            "",
            "Transaction Summary",
            "================================================================================",
            "Upgrade  3 Packages",
            "Downloading Packages:",
            "(1/3): tzdata-2024b-2.el9.noarch.rpm            1.2 MB/s | 841 kB     00:00",
            "(2/3): openssl-3.0.7-27.el9.x86_64.rpm          1.1 MB/s | 1.2 MB     00:01",
            "Running transaction",
            "  Preparing        :                                                        1/1",
            "  Upgrading        : openssl-libs-1:3.0.7-27.el9.x86_64                     1/6",
            "  Upgrading        : openssl-1:3.0.7-27.el9.x86_64                          2/6",
            "  Upgrading        : tzdata-2024b-2.el9.noarch                              3/6",
            "  Cleanup          : openssl-1:3.0.7-25.el9.x86_64                          4/6",
            "  Verifying        : openssl-libs-1:3.0.7-27.el9.x86_64                     1/6",
            "  Verifying        : openssl-1:3.0.7-27.el9.x86_64                          2/6",
            "  Verifying        : tzdata-2024b-2.el9.noarch                              3/6",
            "  Verifying        : tzdata-2024a-1.el9.noarch                              6/6",
            "Upgraded:",
            "  openssl-1:3.0.7-27.el9.x86_64  openssl-libs-1:3.0.7-27.el9.x86_64  tzdata-2024b-2.el9.noarch",
            "Complete!",
        ];
        var (messages, tracker) = LinuxRun(LinuxPackageManager.Dnf, [dnf]);
        check(tracker.Packages.SequenceEqual(["openssl", "openssl-libs", "tzdata"]), $"dnf: rows from the transaction table (got {string.Join(", ", tracker.Packages)})");
        check(tracker.Packages.All(p => tracker.PhaseOf(p) == WingetPhase.Succeeded), "dnf: Upgrading -> Verifying -> Succeeded");
        check(messages.Any(m => m.PackageId == "openssl-libs" && m.Phase == WingetPhase.Installing && m.LogLine.Contains("Upgrading")),
            "dnf: 'Upgrading : openssl-libs-1:3.0.7-27.el9.x86_64' -> Installing openssl-libs");
        check(messages.Any(m => m.PackageId == "tzdata" && m.Phase == WingetPhase.Downloading), "dnf: '(1/3): x.rpm' -> Downloading");
        check(messages.First(m => m.PackageId == LinuxUpgradeTracker.SystemId).Phase == WingetPhase.Downloading &&
              messages.Any(m => m.PackageId == LinuxUpgradeTracker.SystemId && m.Phase == WingetPhase.Installing),
            "dnf: system row Refreshing until 'Dependencies resolved.', then Upgrading");

        var (dnf5, t5) = LinuxRun(LinuxPackageManager.Dnf, [["Updating and loading repositories:", "[1/4] Upgrading openssl-libs-1:3.2.2-9.fc41.x86_64 100% |  1.2 MiB/s"]]);
        check(t5.Packages.SequenceEqual(["openssl-libs"]) && t5.PhaseOf("openssl-libs") == WingetPhase.Succeeded,
            "dnf5: '[n/m] Upgrading x' row, settled Succeeded on exit 0; 'Updating and loading repositories' is not a package");

        var (nothing, _) = LinuxRun(LinuxPackageManager.Dnf, [["Last metadata expiration check: 0:00:01 ago.", "Dependencies resolved.", "Nothing to do.", "Complete!"]]);
        check(nothing[^1].Phase == WingetPhase.Skipped, "dnf: 'Nothing to do.' -> up to date");

        string[] yum =
        [
            "Loaded plugins: fastestmirror",
            "Updating Subscription Management repositories.",
            "Resolving Dependencies",
            "Updating:",
            " openssl-libs        x86_64       1:1.0.2k-26.el7_9         updates       1.2 M",
            "Running transaction",
            "  Updating   : 1:openssl-libs-1.0.2k-26.el7_9.x86_64                        1/2",
            "  Cleanup    : 1:openssl-libs-1.0.2k-25.el7_9.x86_64                        2/2",
            "  Verifying  : 1:openssl-libs-1.0.2k-26.el7_9.x86_64                        1/2",
            "  Verifying  : 1:openssl-libs-1.0.2k-25.el7_9.x86_64                        2/2",
            "Complete!",
        ];
        var (_, ty) = LinuxRun(LinuxPackageManager.Yum, [yum]);
        check(ty.Packages.SequenceEqual(["openssl-libs"]) && ty.PhaseOf("openssl-libs") == WingetPhase.Succeeded,
            $"yum: 'Updating : 1:name-ver' rows, epoch stripped (got {string.Join(", ", ty.Packages)})");
        var (yumNothing, _) = LinuxRun(LinuxPackageManager.Yum, [["Loaded plugins: fastestmirror", "No packages marked for update"]]);
        check(yumNothing[^1].Phase == WingetPhase.Skipped, "yum: 'No packages marked for update' -> up to date");
    }

    private static void LinuxZypper(Action<bool, string> check)
    {
        string[] refresh = ["Retrieving repository 'Main Update Repository' metadata ...........[done]", "Building repository 'Main Update Repository' cache ....[done]", "All repositories have been refreshed."];
        string[] update =
        [
            "Loading repository data...",
            "Reading installed packages...",
            "",
            "The following 2 packages are going to be upgraded:",
            "  libopenssl3 openssl-3",
            "",
            "2 packages to upgrade.",
            "Retrieving: libopenssl3-3.1.4-9.1.x86_64 (Main Update Repository) (1/2),   1.9 MiB",
            "Retrieving: libopenssl3-3.1.4-9.1.x86_64.rpm ..................................[done]",
            "Retrieving: openssl-3-3.1.4-9.1.x86_64 (Main Update Repository) (2/2), 400.0 KiB",
            "Checking for file conflicts: .....................[done]",
            "(1/2) Installing: libopenssl3-3.1.4-9.1.x86_64 ..............................[done]",
            "(2/2) Installing: openssl-3-3.1.4-9.1.x86_64 ..................................[done]",
        ];
        var (messages, tracker) = LinuxRun(LinuxPackageManager.Zypper, [refresh, update]);
        check(tracker.Packages.SequenceEqual(["libopenssl3", "openssl-3"]), $"zypper: rows from 'going to be upgraded' (got {string.Join(", ", tracker.Packages)})");
        check(tracker.Packages.All(p => tracker.PhaseOf(p) == WingetPhase.Succeeded), "zypper: 'Installing: x ...[done]' -> Succeeded");
        check(messages.Any(m => m.PackageId == "openssl-3" && m.Phase == WingetPhase.Downloading), "zypper: 'Retrieving: x' -> Downloading");
        check(!messages.Any(m => m.PackageId == "repository"), "zypper: repository metadata is not a package");
        var (nothing, _) = LinuxRun(LinuxPackageManager.Zypper, [refresh, ["Loading repository data...", "Nothing to do."]]);
        check(nothing[^1].Phase == WingetPhase.Skipped, "zypper: 'Nothing to do.' -> up to date");
    }

    private static void LinuxPacman(Action<bool, string> check)
    {
        string[] run =
        [
            ":: Synchronizing package databases...",
            " core downloading...",
            " extra downloading...",
            ":: Starting full system upgrade...",
            "resolving dependencies...",
            "looking for conflicting packages...",
            "",
            "Packages (2) linux-6.11.9.arch1-1  openssl-3.4.0-1",
            "",
            "Total Download Size:   140.32 MiB",
            ":: Proceed with installation? [Y/n] ",
            ":: Retrieving packages...",
            " linux-6.11.9.arch1-1-x86_64 downloading...",
            " openssl-3.4.0-1-x86_64 downloading...",
            "checking keyring...",
            ":: Processing package changes...",
            "(1/2) upgrading linux                                    [##########################] 100%",
            "(2/2) upgrading openssl                                  [##########################] 100%",
            ":: Running post-transaction hooks...",
        ];
        var (messages, tracker) = LinuxRun(LinuxPackageManager.Pacman, [run]);
        check(tracker.Packages.SequenceEqual(["linux", "openssl"]), $"pacman: rows from 'Packages (n)' (got {string.Join(", ", tracker.Packages)})");
        check(tracker.Packages.All(p => tracker.PhaseOf(p) == WingetPhase.Succeeded), "pacman: '(n/m) upgrading x ... 100%' -> Succeeded");
        check(messages.Any(m => m.PackageId == "linux" && m.Phase == WingetPhase.Downloading), "pacman: 'x-ver-rel-arch downloading' -> Downloading");
        check(!messages.Any(m => m.PackageId is "core" or "extra"), "pacman: database downloads are not packages");
        var stage = messages.Where(m => m.PackageId == LinuxUpgradeTracker.SystemId && !m.Done).Select(m => m.Phase).Distinct().ToList();
        check(stage.SequenceEqual([WingetPhase.Downloading, WingetPhase.Installing]), "pacman: Refreshing until ':: Starting full system upgrade', then Upgrading");
        var (old, t2) = LinuxRun(LinuxPackageManager.Pacman, [["upgrading openssl..."]]);
        check(t2.PhaseOf("openssl") == WingetPhase.Succeeded, "pacman (old format): 'upgrading x...' row");
        var (nothing, _) = LinuxRun(LinuxPackageManager.Pacman, [[":: Starting full system upgrade...", " there is nothing to do"]]);
        check(nothing[^1].Phase == WingetPhase.Skipped, "pacman: 'there is nothing to do' -> up to date");
    }

    private static void NevraNames(Action<bool, string> check)
    {
        (string In, string Out)[] cases =
        [
            ("openssl-libs-1:3.0.7-27.el9.x86_64", "openssl-libs"),
            ("1:openssl-libs-1.0.2k-26.el7_9.x86_64", "openssl-libs"),
            ("tzdata-2024b-2.el9.noarch", "tzdata"),
            ("libopenssl3-3.1.4-9.1.x86_64.rpm", "libopenssl3"),
            ("openssl-3-3.1.4-9.1.x86_64", "openssl-3"),
            ("linux-6.11.9.arch1-1", "linux"),
            ("linux-6.11.9.arch1-1-x86_64", "linux"),
            ("python3-dnf-plugins-core-4.3.0-16.el9.noarch", "python3-dnf-plugins-core"),
        ];
        foreach (var (input, expected) in cases)
        {
            var got = LinuxUpgradeParser.NameFromNevra(input);
            check(got == expected, $"package name: '{input}' -> '{expected}'{(got == expected ? "" : $" (got '{got}')")}");
        }
    }
}
