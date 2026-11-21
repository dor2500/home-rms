using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Controller.Views.Deploy;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

namespace HomeRms.Controller;

/// <summary>
/// Deploy tab, install side: the Install view (Deploy to endpoints | Batch | PowerShell | Winget Import),
/// script generation, target endpoints, the streaming RunWinget / WinRM deployment and the Files tab.
/// </summary>
public partial class MainWindow
{
    /// <summary>Packages that share one RunWinget call: same scope and (when pinned) a single package.</summary>
    private sealed record WingetBatch(List<WingetAppItem> Items, bool MachineScope, string? Version);

    private void InitializeInstallView()
    {
        InstallV.OptionsChanged += (_, _) => RegenerateScript();
        InstallV.CopyScriptBtn.Click += (_, _) => CopyText(InstallV.ScriptBox.Text, () => InstallV.CopyScriptText.Text = "Copied!");
        InstallV.DownloadScriptBtn.Click += OnDownloadScriptClick;
        InstallV.DeployBtn.Click += OnDeploySelectedWingetClick;
        InstallV.CancelBtn.Click += OnCancelWingetDeployClick;
        InstallV.RefreshTargetsBtn.Click += (_, _) => RefreshDeployTargets();
        InstallV.ClearLogBtn.Click += OnClearDeployLogClick;
        InstallV.AddMoreBtn.Click += (_, _) => DeployShell.Show(DeployView.Apps);
        InstallV.BackBtn.Click += (_, _) => DeployShell.Show(DeployShell.Previous);
        InitializeUpdateAll(); // MainWindow.UpdateAll.cs

        FilesV.BrowseBtn.Click += OnBrowseDeployFileClick;
        FilesV.RunFilesBtn.Click += OnStartDeployClick;
        FilesV.RunScriptBtn.Click += OnUploadAndRunScriptClick;
        FilesV.ClearFilesBtn.Click += OnClearDeployFilesClick;
        FilesV.ClearLogBtn.Click += OnClearDeployLogClick;
        FilesV.RemoveRequested += (_, item) =>
        {
            if (item is DeployFileItem { IsBusy: false } f) _deployFiles.Remove(f);
        };
    }

    /// <summary>Install view opened: make sure every selected app has a version list (winstall details, else index latest).</summary>
    private void PrepareInstallView()
    {
        RefreshDeployTargets();
        foreach (var item in _selectedApps)
        {
            item.EnsureVersionOptions();
            if (!item.DetailsLoaded) _ = LoadAppDetailsAsync(item, toPage: false);
        }
        RegenerateScript();
    }

    private void OnSelectedAppOptionsChanged(object? sender, EventArgs e) => RegenerateScript();

    // ---------------- Script generation ----------------
    private static WingetScriptApp ToScriptApp(WingetAppItem item)
    {
        var a = item.Advanced;
        return a.UseCustom
            ? new WingetScriptApp(item.Id, item.PinnedVersion, a.Scope, a.Interactive, a.Force,
                NullIfBlank(a.Override), NullIfBlank(a.Log), NullIfBlank(a.Location))
            : new WingetScriptApp(item.Id, item.PinnedVersion);
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void RegenerateScript()
    {
        if (!_deployInitialized && InstallV is null) return;
        var tab = InstallV.ActiveTab;
        if (tab == InstallTab.Deploy) return;

        var apps = _selectedApps.Where(a => WingetRules.IsValidPackageId(a.Id)).Select(ToScriptApp).ToList();
        if (apps.Count == 0)
        {
            InstallV.ScriptBox.Text = "";
            InstallV.ScriptNote.Text = "Select at least one app to generate a script.";
            InstallV.ScriptNote.Foreground = Themes.Palette.Get("DeployMutedTextBrush");
            return;
        }

        try
        {
            var options = InstallV.Options;
            InstallV.ScriptBox.Text = tab switch
            {
                InstallTab.Batch => WingetScriptGenerator.Batch(apps, options),
                InstallTab.PowerShell => WingetScriptGenerator.PowerShell(apps, options),
                _ => WingetScriptGenerator.WingetImportJson(apps),
            };
            InstallV.ScriptNote.Text = tab switch
            {
                InstallTab.Batch => $"{apps.Count} app(s). Save as .bat and run it; use an elevated prompt for machine-scope installs.",
                InstallTab.PowerShell => $"{apps.Count} app(s). Paste into PowerShell or save as .ps1; use an elevated session for machine-scope installs.",
                _ => $"{apps.Count} package(s). Run: winget import -i winget-import.json --accept-package-agreements --accept-source-agreements (scope and advanced options are not part of the import format).",
            };
            InstallV.ScriptNote.Foreground = Themes.Palette.Get("DeployMutedTextBrush");
        }
        catch (ArgumentException ex)
        {
            InstallV.ScriptBox.Text = "";
            InstallV.ScriptNote.Text = $"Cannot generate this script: {ex.Message}";
            InstallV.ScriptNote.Foreground = Themes.Palette.Danger;
        }
        InstallV.CopyScriptText.Text = "Copy to clipboard";
    }

    private void OnDownloadScriptClick(object sender, RoutedEventArgs e)
    {
        var text = InstallV.ScriptBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        var (ext, filter, encoding) = InstallV.ActiveTab switch
        {
            InstallTab.PowerShell => (".ps1", "PowerShell script (*.ps1)|*.ps1", (Encoding)new UTF8Encoding(true)),
            InstallTab.WingetImport => (".json", "winget import file (*.json)|*.json", new UTF8Encoding(false)),
            _ => (".bat", "Batch file (*.bat)|*.bat", new UTF8Encoding(false)),
        };
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save install script",
            FileName = InstallV.ActiveTab == InstallTab.WingetImport ? "winget-import.json" : "homerms-install" + ext,
            DefaultExt = ext,
            Filter = filter + "|All files (*.*)|*.*",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, text, encoding);
            StatusText.Text = $"Saved {Path.GetFileName(dlg.FileName)}.";
            InstallV.ScriptNote.Text = $"Saved to {dlg.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save the script: {ex.Message}";
        }
    }

    // ---------------- Targets ----------------
    private void RefreshDeployTargets()
    {
        var wasChecked = _deployTargets.Where(t => t.IsChecked).Select(t => t.Profile).ToHashSet();
        _deployTargets.Clear();
        // Linux endpoints are listed for Update All (Linux agents) and skipped by app deploys.
        foreach (var m in _machines.Where(m => m.IsWindows || m.Kind == EndpointKind.Linux))
        {
            var mode = m.IsAgentless ? "WinRM" : m.Kind != EndpointKind.Linux ? "agent"
                : IsLinuxAgent(m) ? "Linux agent · Update All only" : "SSH · not supported";
            var state = m.IsConnected ? "connected" : m.IsOnline ? "online" : "offline";
            _deployTargets.Add(new DeployTarget { Profile = m, Detail = $"  {mode} · {state}", IsChecked = wasChecked.Contains(m) });
        }
        InstallV.TargetHint.Text = _deployTargets.Count == 0
            ? "No saved Windows endpoints: deploys go to the connected session."
            : "Nothing checked = the connected (or selected WinRM) endpoint. Check several to deploy to each in turn. Update All upgrades every installed package.";
    }

    private async Task<List<WingetTarget>?> ResolveWingetTargetsAsync()
    {
        var picked = _deployTargets.Where(t => t.IsChecked).Select(t => t.Profile).ToList();
        if (picked.Count > 0)
            return picked.Select(p => new WingetTarget(p.DisplayTitle, p, p.IsAgentless)).ToList();

        if (SelectedMachine is { IsAgentless: true } agentless)
            return [new WingetTarget(agentless.DisplayTitle, agentless, true)];

        if (_conn is null && !await ConnectToCurrentFieldsAsync())
            return null;
        return [new WingetTarget(_agentInfo?.MachineName ?? _connectedProfile?.DisplayTitle ?? _conn!.Host, _connectedProfile, false)];
    }

    /// <summary>
    /// Groups the selection for RunWinget: unpinned apps with the same scope share calls (up to the agent's
    /// per-request limit); every app with a pinned version gets its own call (the proto's version is single-package).
    /// </summary>
    private static List<WingetBatch> BuildWingetBatches(IReadOnlyList<WingetAppItem> items, InstallScope defaultScope)
    {
        bool Machine(WingetAppItem i) => (i.Advanced.UseCustom ? i.Advanced.Scope : defaultScope) == InstallScope.Machine;
        var batches = new List<WingetBatch>();
        foreach (var group in items.Where(i => i.PinnedVersion is null).GroupBy(Machine))
            foreach (var chunk in group.Chunk(WingetRules.MaxPackagesPerRequest))
                batches.Add(new WingetBatch(chunk.ToList(), group.Key, null));
        foreach (var item in items.Where(i => i.PinnedVersion is not null))
            batches.Add(new WingetBatch([item], Machine(item), item.PinnedVersion));
        return batches;
    }

    // ---------------- Deploy ----------------
    private void OnCancelWingetDeployClick(object sender, RoutedEventArgs e)
    {
        if (_wingetDeployCts is null) return;
        _wingetDeployCts.Cancel();
        InstallV.CancelBtn.IsEnabled = false;
        DeployStatusBadge.Text = "CANCELLING...";
        DeployLog("winget", null, "Cancellation requested.");
    }

    private async void OnDeploySelectedWingetClick(object sender, RoutedEventArgs e)
    {
        if (_wingetDeployCts is not null) return;
        var items = _selectedApps.Where(i => WingetRules.IsValidPackageId(i.Id)).ToList();
        if (items.Count == 0)
        {
            StatusText.Text = "Select at least one app to deploy.";
            return;
        }

        List<WingetTarget>? targets;
        try
        {
            targets = await ResolveWingetTargetsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not resolve deployment targets: {ex.Message}";
            return;
        }
        if (targets is null || targets.Count == 0) return;
        var nonWindows = targets.Where(t => t.Profile is { IsWindows: false }).ToList();
        foreach (var t in nonWindows)
            DeployLog(t.Label, null, "Skipped: app deploys need a Windows endpoint (Linux agents support Update All).");
        targets = targets.Except(nonWindows).ToList();
        if (targets.Count == 0)
        {
            StatusText.Text = "None of the checked endpoints runs Windows: app deploys use winget.";
            return;
        }
        var targetCount = targets.Count;

        var options = InstallV.Options;
        var silent = options.Silent;
        var upgrade = options.UpgradeIfInstalled;
        var batches = BuildWingetBatches(items, options.Scope);

        var cts = _wingetDeployCts = new CancellationTokenSource();
        SetDeployButtonsBusy(true);
        ShowUpgradeQueue(false);
        DeployStatusBadge.Text = "DEPLOYING...";

        var outcomes = items.ToDictionary(i => i, _ => new List<WingetOutcome>());
        void Record(WingetAppItem item, string target, DeployItemStatus status, string detail)
        {
            if (outcomes.TryGetValue(item, out var l)) l.Add(new WingetOutcome(target, status, detail));
            item.DeployStatus = status;
            item.Percent = -1;
            item.StatusDetail = targetCount > 1 ? $"{target}: {detail}" : detail;
        }

        foreach (var item in items)
        {
            item.ResetDeployState();
            item.DeployStatus = DeployItemStatus.Queued;
        }
        var pinned = items.Where(i => i.PinnedVersion is not null).Select(i => $"{i.Id}@{i.PinnedVersion}").ToList();
        DeployLog("winget", null, $"Deploying {items.Count} package(s) to {targets.Count} endpoint(s): {string.Join(", ", targets.Select(t => t.Label))} " +
                                  $"(silent={silent}, default scope={options.Scope.ToString().ToLowerInvariant()}, upgrade if installed={upgrade}" +
                                  $"{(pinned.Count > 0 ? $", pinned: {string.Join(", ", pinned)}" : "")}).");
        if (options.Interactive || options.Force || items.Any(i => i.Advanced.UseCustom && (i.Advanced.Interactive || i.Advanced.Force || i.Advanced.Override.Length > 0 || i.Advanced.Log.Length > 0 || i.Advanced.Location.Length > 0)))
            DeployLog("winget", null, "Note: --interactive, --force, --override, --log and --location only apply to generated scripts; remote deploys run unattended with validated options.");

        try
        {
            foreach (var target in targets)
            {
                if (cts.IsCancellationRequested) break;
                foreach (var item in items)
                {
                    item.Percent = -1;
                    item.DeployStatus = DeployItemStatus.Queued;
                }

                if (target.Agentless)
                    await DeployWingetAgentlessAsync(target, batches, silent, upgrade, Record, cts.Token);
                else
                    await DeployWingetViaAgentAsync(target, batches, silent, upgrade, Record, cts.Token);
            }
        }
        catch (Exception ex)
        {
            DeployLog("winget", null, $"Deployment aborted: {ex.Message}");
        }
        finally
        {
            int ok = 0, skipped = 0, failed = 0, cancelled = 0;
            foreach (var item in items)
            {
                var list = outcomes[item];
                var reported = list.Select(o => o.Target).ToHashSet();
                foreach (var t in targets.Where(t => !reported.Contains(t.Label)))
                    list.Add(new WingetOutcome(t.Label, DeployItemStatus.Cancelled, "Not run (cancelled)"));

                var status = list.Any(o => o.Status == DeployItemStatus.Failed) ? DeployItemStatus.Failed
                    : list.Any(o => o.Status == DeployItemStatus.Cancelled) ? DeployItemStatus.Cancelled
                    : list.All(o => o.Status == DeployItemStatus.Skipped) ? DeployItemStatus.Skipped
                    : DeployItemStatus.Success;
                item.Percent = -1;
                item.DeployStatus = status;
                item.StatusDetail = targets.Count > 1
                    ? string.Join("\n", list.Select(o => $"{o.Target}: {o.Detail}"))
                    : list.FirstOrDefault()?.Detail ?? "";

                switch (status)
                {
                    case DeployItemStatus.Success: ok++; break;
                    case DeployItemStatus.Skipped: skipped++; break;
                    case DeployItemStatus.Failed: failed++; break;
                    default: cancelled++; break;
                }
            }

            var wasCancelled = cts.IsCancellationRequested;
            _wingetDeployCts = null;
            cts.Dispose();
            SetDeployButtonsBusy(false);
            DeployStatusBadge.Text = wasCancelled ? "CANCELLED" : failed > 0 ? "COMPLETED WITH ERRORS" : "COMPLETE";
            var summary = $"Finished: {ok} installed, {skipped} up to date, {failed} failed, {cancelled} cancelled.";
            DeployLog("winget", null, summary);
            StatusText.Text = $"Winget deployment {summary}";
        }
    }

    private async Task DeployWingetViaAgentAsync(WingetTarget target, List<WingetBatch> batches, bool silent, bool upgrade,
        Action<WingetAppItem, string, DeployItemStatus, string> record, CancellationToken ct)
    {
        var all = batches.SelectMany(b => b.Items).ToList();
        var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? failure = null;
        var failureStatus = DeployItemStatus.Failed;

        try
        {
            var conn = await ResolveAgentConnectionAsync(target.Profile, ct);
            if (conn is null)
            {
                failure = "Not connected to an agent.";
                return;
            }

            foreach (var batch in batches)
            {
                ct.ThrowIfCancellationRequested();
                var byId = batch.Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
                var request = new WingetRequest { Silent = silent, MachineScope = batch.MachineScope, UpgradeIfInstalled = upgrade };
                request.PackageIds.AddRange(batch.Items.Select(i => i.Id));
                if (batch.Version is not null) request.Version = batch.Version;
                DeployLog(target.Label, null, $"RunWinget: {string.Join(", ", batch.Items.Select(i => i.Id))}" +
                                              $"{(batch.Version is null ? "" : $" (version {batch.Version})")}{(batch.MachineScope ? " (machine scope)" : "")}");

                using var call = conn.Client.RunWinget(request, cancellationToken: ct);
                await foreach (var progress in call.ResponseStream.ReadAllAsync(ct))
                {
                    byId.TryGetValue(progress.PackageId ?? "", out var item);
                    if (!string.IsNullOrEmpty(progress.LogLine))
                        DeployLog(target.Label, string.IsNullOrEmpty(progress.PackageId) ? null : progress.PackageId, progress.LogLine);

                    if (item is not null)
                    {
                        switch (progress.Phase)
                        {
                            case WingetPhase.Queued:
                                item.DeployStatus = DeployItemStatus.Queued;
                                break;
                            case WingetPhase.Downloading:
                                item.DeployStatus = DeployItemStatus.Downloading;
                                item.Percent = progress.Percent;
                                break;
                            case WingetPhase.Installing:
                                item.DeployStatus = DeployItemStatus.Installing;
                                item.Percent = progress.Percent;
                                break;
                            case WingetPhase.Succeeded:
                            case WingetPhase.Failed:
                            case WingetPhase.Skipped:
                                if (finished.Add(item.Id))
                                {
                                    var status = progress.Phase switch
                                    {
                                        WingetPhase.Succeeded => DeployItemStatus.Success,
                                        WingetPhase.Skipped => DeployItemStatus.Skipped,
                                        _ => DeployItemStatus.Failed,
                                    };
                                    var detail = DescribeExit(progress.ExitCode, progress.ExitMessage);
                                    record(item, target.Label, status, detail);
                                    DeployLog(target.Label, item.Id, $"{progress.Phase}: {detail}");
                                }
                                break;
                        }
                    }
                    if (progress.Done) break;
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated)
        {
            failure = PairFirstMessage(ex.StatusCode);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled || ct.IsCancellationRequested)
        {
            failure = "Cancelled.";
            failureStatus = DeployItemStatus.Cancelled;
        }
        catch (OperationCanceledException)
        {
            failure = "Cancelled.";
            failureStatus = DeployItemStatus.Cancelled;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            failure = "This agent does not support RunWinget; update the agent to v2.1 or newer.";
        }
        catch (RpcException ex)
        {
            failure = $"{ex.StatusCode}: {ex.Status.Detail}";
        }
        catch (Exception ex)
        {
            failure = ex is AgentPairingException ? AgentErrors.Describe(ex) : ex.Message;
        }

        if (failure is not null)
            DeployLog(target.Label, null, failure);
        foreach (var item in all.Where(i => !finished.Contains(i.Id)))
            record(item, target.Label, failure is null ? DeployItemStatus.Failed : failureStatus,
                failure ?? "The agent ended the stream without a result for this package.");
    }

    /// <summary>The live session when it is this endpoint, else a pooled + paired connection; null when there is neither.</summary>
    private async Task<RmsConnection?> ResolveAgentConnectionAsync(MachineProfile? p, CancellationToken ct)
    {
        if (_conn is not null && (p is null || p == _connectedProfile ||
                                  (string.Equals(_conn.Host, p.Host, StringComparison.OrdinalIgnoreCase) && _conn.Port == p.Port)))
            return _conn;
        // Pooled + paired; the pool also follows an endpoint whose IP changed (re-resolve, one retry).
        return p is null ? null : await _pool.GetPairedAsync(p, interactive: true, ct);
    }

    private static string PairFirstMessage(StatusCode code) =>
        $"Endpoint refused the request ({code}): pair this endpoint first. Connect to it from Fleet and enter the 2FA code shown on the agent so it pins this Controller, then retry.";

    private async Task DeployWingetAgentlessAsync(WingetTarget target, List<WingetBatch> batches, bool silent, bool upgrade,
        Action<WingetAppItem, string, DeployItemStatus, string> record, CancellationToken ct)
    {
        var profile = target.Profile!;
        if (string.IsNullOrWhiteSpace(profile.Username))
        {
            const string msg = "WinRM credentials are missing for this endpoint (edit it in Fleet).";
            DeployLog(target.Label, null, msg);
            foreach (var item in batches.SelectMany(b => b.Items)) record(item, target.Label, DeployItemStatus.Failed, msg);
            return;
        }

        foreach (var batch in batches)
        foreach (var item in batch.Items)
        {
            if (ct.IsCancellationRequested)
            {
                record(item, target.Label, DeployItemStatus.Cancelled, "Cancelled.");
                continue;
            }

            item.DeployStatus = DeployItemStatus.Installing;
            item.Percent = -1;
            DeployLog(target.Label, item.Id, $"Running winget over WinRM{(batch.Version is null ? "" : $" (version {batch.Version})")}...");
            try
            {
                var script = WingetCatalogService.BuildWingetInstallScript(item.Id, silent, batch.MachineScope, upgrade, batch.Version);
                var (_, stdout, stderr) = await _agentless.InvokeWinRmAsync(profile, script, ct, timeoutMs: 30 * 60 * 1000);

                foreach (var line in stdout.Split('\n'))
                    if (!line.Contains(WingetCatalogService.ExitMarker, StringComparison.Ordinal))
                        DeployLog(target.Label, item.Id, line);
                foreach (var line in stderr.Split('\n'))
                    DeployLog(target.Label, item.Id, line.Trim().Length == 0 ? "" : "stderr: " + line);

                if (ct.IsCancellationRequested)
                {
                    record(item, target.Label, DeployItemStatus.Cancelled, "Cancelled.");
                    continue;
                }

                var code = WingetCatalogService.ParseExitMarker(stdout);
                if (code is null)
                {
                    var why = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no exit-code marker in the WinRM output";
                    record(item, target.Label, DeployItemStatus.Failed, $"WinRM run failed: {why}");
                    continue;
                }

                var status = WingetRules.IsSuccess(code.Value) ? DeployItemStatus.Success
                    : WingetRules.IsAlreadyInstalled(code.Value) ? DeployItemStatus.Skipped
                    : DeployItemStatus.Failed;
                var detail = DescribeExit(code.Value, null);
                record(item, target.Label, status, detail);
                DeployLog(target.Label, item.Id, detail);
            }
            catch (Exception ex)
            {
                record(item, target.Label, ct.IsCancellationRequested ? DeployItemStatus.Cancelled : DeployItemStatus.Failed, ex.Message);
                DeployLog(target.Label, item.Id, $"ERROR: {ex.Message}");
            }
        }
    }

    private static string DescribeExit(int exitCode, string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? WingetRules.Describe(exitCode) : message;
        var hex = $"0x{unchecked((uint)exitCode):X8}";
        return text.Contains(hex, StringComparison.OrdinalIgnoreCase) || exitCode == 0
            ? $"exit {exitCode}: {text}"
            : $"exit {exitCode} ({hex}): {text}";
    }

    /// <summary>Timestamped log line with endpoint / package prefix; control characters are stripped.</summary>
    private void DeployLog(string scope, string? package, string message)
    {
        var clean = new string(message.Where(c => c == '\t' || !char.IsControl(c)).ToArray()).TrimEnd();
        if (clean.Length == 0) return;
        AppendDeployLog($"[{DateTime.Now:HH:mm:ss}] [{scope}]{(package is null ? "" : $" [{package}]")} {clean}");
    }

    // ---------------- File deploy list ----------------
    private static readonly string[] DeployableExtensions = [".exe", ".msi", ".msp", ".ps1", ".cmd", ".bat"];

    private static bool IsDeployableFile(string path) =>
        DeployableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private DeployFileItem? AddDeployFile(string path)
    {
        if (!File.Exists(path) || !IsDeployableFile(path))
        {
            DeployLog("file", null, $"Skipped '{path}': only existing .exe, .msi, .msp, .ps1, .cmd and .bat files can be deployed.");
            return null;
        }
        var existing = _deployFiles.FirstOrDefault(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!existing.IsBusy) { existing.DeployStatus = DeployItemStatus.Pending; existing.Detail = ""; existing.Percent = -1; }
            return existing;
        }
        var item = new DeployFileItem { FullPath = path };
        item.Detail = $"{new FileInfo(path).Length / 1024.0 / 1024.0:F2} MB";
        _deployFiles.Add(item);
        return item;
    }

    private void OnClearDeployFilesClick(object sender, RoutedEventArgs e)
    {
        foreach (var f in _deployFiles.Where(f => !f.IsBusy).ToList())
            _deployFiles.Remove(f);
    }

    /// <summary>Uploads and runs every pending file in the list, one after another, on the connected agent.</summary>
    private async Task RunPendingDeployFilesAsync()
    {
        if (_fileDeployRunning) return;
        var pending = _deployFiles.Where(f => f.DeployStatus is DeployItemStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            StatusText.Text = "No pending files: drop installers/scripts here or use Browse.";
            return;
        }

        _fileDeployRunning = true;
        try
        {
            foreach (var f in pending)
            {
                if (!_deployFiles.Contains(f)) continue;
                if (!await DeployLocalFileCoreAsync(f, DeployArgsBox.Text.Trim()))
                {
                    if (_conn is null) break; // not connected: stop instead of failing every file
                }
            }
        }
        finally
        {
            _fileDeployRunning = false;
        }
    }

    private bool _fileDeployRunning;
}
