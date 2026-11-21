using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
/// Deploy → Install → "Update All": upgrades every installed package on the target endpoints (winget
/// upgrade --all through the UpgradeAll RPC or WinRM; the package manager on Linux agents). Up to three
/// endpoints run at once; the Progress panel shows one summary row per endpoint plus one row per package,
/// and Cancel stops every run (the agents kill their process trees).
/// </summary>
public partial class MainWindow
{
    private const int UpdateAllParallelism = 3;
    private readonly ObservableCollection<UpgradeRow> _upgradeRows = [];
    private bool _showingUpgradeRows;

    private enum UpdateOutcome { Upgraded, UpToDate, Failed, Cancelled, Skipped }

    private void InitializeUpdateAll()
    {
        InstallV.UpdateAllBtn.Click += OnUpdateAllClick;
        DiscoverV.UpdateAllBtn.Click += (s, e) =>
        {
            DeployShell.Show(DeployView.Install);
            InstallV.SelectTab(InstallTab.Deploy);
            OnUpdateAllClick(s, e);
        };
        // UpdateWingetSelectedCount (MainWindow.Deploy.cs) resets the empty-queue hint for the app list.
        _selectedApps.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(SyncQueueEmptyText);
        _upgradeRows.CollectionChanged += (_, _) => SyncQueueEmptyText();
    }

    /// <summary>Switches the Progress panel between the selected apps (Deploy) and the Update All rows.</summary>
    private void ShowUpgradeQueue(bool upgrade)
    {
        _showingUpgradeRows = upgrade;
        InstallV.QueueList.ItemsSource = upgrade ? _upgradeRows : _selectedApps;
        InstallV.QueueTitle.Text = upgrade ? "Progress · Update All" : "Progress";
        SyncQueueEmptyText();
    }

    private void SyncQueueEmptyText()
    {
        var empty = _showingUpgradeRows ? _upgradeRows.Count == 0 : _selectedApps.Count == 0;
        InstallV.QueueEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetDeployButtonsBusy(bool busy)
    {
        InstallV.DeployBtn.IsEnabled = !busy;
        InstallV.UpdateAllBtn.IsEnabled = !busy;
        DiscoverV.UpdateAllBtn.IsEnabled = !busy;
        InstallV.CancelBtn.IsEnabled = busy;
    }

    private static bool IsLinuxAgent(MachineProfile p) => p.IsLinuxAgent;

    private static string DescribeUpdateTarget(WingetTarget t) => t.Profile switch
    {
        null => "connected agent",
        { IsAgentless: true } => "WinRM · winget",
        var p when IsLinuxAgent(p) => "Linux agent · package manager",
        { IsAppliance: true } => "SSH appliance · will be skipped",
        _ => "agent · winget",
    };

    private async void OnUpdateAllClick(object sender, RoutedEventArgs e)
    {
        if (_wingetDeployCts is not null)
        {
            StatusText.Text = "A deployment is already running: wait for it to finish or cancel it first.";
            return;
        }

        List<WingetTarget>? targets;
        try
        {
            targets = await ResolveWingetTargetsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not resolve the target endpoints: {ex.Message}";
            return;
        }
        if (targets is null || targets.Count == 0 || _wingetDeployCts is not null) return;

        var includeUnknown = InstallV.IncludeUnknown;
        var silent = InstallV.Options.Silent;
        var list = string.Join("\n", targets.Select(t => $"  •  {t.Label}   ({DescribeUpdateTarget(t)})"));
        var prompt =
            $"Upgrade ALL installed packages on {targets.Count} endpoint{(targets.Count == 1 ? "" : "s")}?\n\n{list}\n\n" +
            $"Windows: winget upgrade --all{(silent ? " --silent" : "")}{(includeUnknown ? " --include-unknown" : "")}\n" +
            "Linux agents: the distribution's package manager (apt-get, dnf, yum, zypper or pacman).\n\n" +
            $"Running apps may be closed or restarted while they are upgraded. Up to {UpdateAllParallelism} endpoints are updated at a time.";
        if (MessageBox.Show(this, prompt, "Home RMS - Update All", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;
        if (_wingetDeployCts is not null) return;

        var cts = _wingetDeployCts = new CancellationTokenSource();
        SetDeployButtonsBusy(true);
        DeployStatusBadge.Text = "UPDATING...";
        _upgradeRows.Clear();
        ShowUpgradeQueue(true);
        DeployLog("update-all", null, $"Update All on {targets.Count} endpoint(s): {string.Join(", ", targets.Select(t => t.Label))} " +
                                      $"(silent={silent}, include unknown={includeUnknown}).");

        var outcomes = new List<UpdateOutcome>();
        using var gate = new SemaphoreSlim(UpdateAllParallelism, UpdateAllParallelism);

        async Task RunOne(WingetTarget target)
        {
            var summary = UpgradeRowFor(target.Label, "");
            summary.Stage = "Waiting";
            try
            {
                await gate.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                FinishRow(summary, DeployItemStatus.Cancelled, "Not run (cancelled).");
                outcomes.Add(UpdateOutcome.Cancelled);
                return;
            }

            try
            {
                outcomes.Add(await UpdateAllOnTargetAsync(target, summary, includeUnknown, silent, cts.Token));
            }
            catch (Exception ex)
            {
                FinishRow(summary, DeployItemStatus.Failed, ex.Message);
                DeployLog(target.Label, null, $"Update All failed: {ex.Message}");
                outcomes.Add(UpdateOutcome.Failed);
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            await Task.WhenAll(targets.Select(RunOne));
        }
        finally
        {
            var wasCancelled = cts.IsCancellationRequested;
            _wingetDeployCts = null;
            cts.Dispose();
            SetDeployButtonsBusy(false);

            int Count(UpdateOutcome o) => outcomes.Count(x => x == o);
            var failed = Count(UpdateOutcome.Failed);
            DeployStatusBadge.Text = wasCancelled ? "CANCELLED" : failed > 0 ? "COMPLETED WITH ERRORS" : "COMPLETE";
            var summary = $"Update All finished: {Count(UpdateOutcome.Upgraded)} updated, {Count(UpdateOutcome.UpToDate)} already up to date, " +
                          $"{failed} failed, {Count(UpdateOutcome.Skipped)} skipped, {Count(UpdateOutcome.Cancelled)} cancelled.";
            DeployLog("update-all", null, summary);
            StatusText.Text = summary;
        }
    }

    private async Task<UpdateOutcome> UpdateAllOnTargetAsync(WingetTarget target, UpgradeRow summary, bool includeUnknown, bool silent, CancellationToken ct)
    {
        if (target.Profile is { } p && p.IsAppliance && !IsLinuxAgent(p))
        {
            const string msg = "Skipped: SSH appliances are not supported by Update All (install the Home RMS Linux agent on it).";
            FinishRow(summary, DeployItemStatus.Skipped, msg);
            DeployLog(target.Label, null, msg);
            return UpdateOutcome.Skipped;
        }
        return target.Agentless
            ? await UpdateAllViaWinRmAsync(target, summary, includeUnknown, silent, ct)
            : await UpdateAllViaAgentAsync(target, summary, includeUnknown, silent, ct);
    }

    private async Task<UpdateOutcome> UpdateAllViaAgentAsync(WingetTarget target, UpgradeRow summary, bool includeUnknown, bool silent, CancellationToken ct)
    {
        string? failure = null;
        var cancelled = false;
        var linux = target.Profile is { } p && IsLinuxAgent(p);
        RmsConnection? conn = null;

        try
        {
            summary.Stage = "Connecting";
            conn = await ResolveAgentConnectionAsync(target.Profile, ct);
            if (conn is null)
            {
                failure = "Not connected to an agent.";
            }
            else
            {
                summary.Stage = "Checking";
                using var call = conn.Client.UpgradeAll(new UpgradeAllRequest { IncludeUnknown = includeUnknown, Silent = silent }, cancellationToken: ct);
                await foreach (var m in call.ResponseStream.ReadAllAsync(ct))
                {
                    if (m.PackageId == LinuxUpgradeTracker.SystemId) linux = true;
                    var outcome = ApplyUpgradeProgress(target.Label, summary, m, linux);
                    if (m.Done) return outcome;
                }
                failure = "The agent ended the stream without a final result.";
            }
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated)
        {
            failure = PairFirstMessage(ex.StatusCode);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled || ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            failure = $"This agent ({await AgentVersionLabelAsync(conn)}) doesn't support Update All — update the agent first.";
        }
        catch (RpcException ex)
        {
            failure = ex.StatusCode == StatusCode.FailedPrecondition ? ex.Status.Detail : $"{ex.StatusCode}: {ex.Status.Detail}";
        }
        catch (Exception ex)
        {
            failure = ex is AgentPairingException ? AgentErrors.Describe(ex) : ex.Message;
        }

        return FailEndpoint(target.Label, summary, cancelled ? null : failure);
    }

    private static async Task<string> AgentVersionLabelAsync(RmsConnection? conn)
    {
        if (conn is null) return "an older version";
        try
        {
            var info = await conn.Client.GetAgentInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(10));
            var v = info.AgentVersion?.Trim() ?? "";
            return v.Length == 0 ? "an older version" : v.StartsWith('v') ? v : "v" + v;
        }
        catch
        {
            return "an older version";
        }
    }

    private async Task<UpdateOutcome> UpdateAllViaWinRmAsync(WingetTarget target, UpgradeRow summary, bool includeUnknown, bool silent, CancellationToken ct)
    {
        var profile = target.Profile!;
        if (string.IsNullOrWhiteSpace(profile.Username))
            return FailEndpoint(target.Label, summary, "WinRM credentials are missing for this endpoint (edit it in Fleet).");

        summary.DeployStatus = DeployItemStatus.Installing;
        summary.Percent = -1;
        summary.Stage = "Upgrading (WinRM)";
        DeployLog(target.Label, null, "Running winget upgrade --all over WinRM (the output arrives when the run finishes)...");
        try
        {
            var script = UpgradeAllScripts.BuildWingetUpgradeAllScript(includeUnknown, silent);
            var (_, stdout, stderr) = await _agentless.InvokeWinRmAsync(profile, script, ct, timeoutMs: 3 * 60 * 60 * 1000);
            if (ct.IsCancellationRequested)
            {
                DeployLog(target.Label, null, "Stopped waiting for WinRM; winget may keep running on the endpoint until it finishes.");
                return FailEndpoint(target.Label, summary, null);
            }

            foreach (var line in stderr.Split('\n'))
                if (line.Trim().Length > 0) DeployLog(target.Label, null, "stderr: " + line);

            var (messages, code) = UpgradeAllScripts.ParseWinRmOutput(stdout);
            if (code is null)
            {
                var why = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no exit-code marker in the WinRM output";
                return FailEndpoint(target.Label, summary, $"WinRM run failed: {why}");
            }

            var outcome = UpdateOutcome.Failed;
            foreach (var m in messages)
            {
                var o = ApplyUpgradeProgress(target.Label, summary, m, linux: false);
                if (m.Done) outcome = o;
            }
            return outcome;
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException)
        {
            return FailEndpoint(target.Label, summary, null);
        }
        catch (Exception ex)
        {
            return FailEndpoint(target.Label, summary, $"WinRM run failed: {ex.Message}");
        }
    }

    /// <summary>Ends an endpoint run without a final agent message. <paramref name="failure"/> null = cancelled.</summary>
    private UpdateOutcome FailEndpoint(string label, UpgradeRow summary, string? failure)
    {
        var cancelled = failure is null;
        if (!cancelled) DeployLog(label, null, failure!);
        foreach (var row in _upgradeRows.Where(r => r.Endpoint == label && !r.IsSummary && r.IsBusy).ToList())
            FinishRow(row, cancelled ? DeployItemStatus.Cancelled : DeployItemStatus.Failed, cancelled ? "Cancelled." : "Not upgraded.");
        FinishRow(summary, cancelled ? DeployItemStatus.Cancelled : DeployItemStatus.Failed, cancelled ? "Cancelled." : failure!);
        return cancelled ? UpdateOutcome.Cancelled : UpdateOutcome.Failed;
    }

    /// <summary>Applies one streamed message to the endpoint's rows and the Live log; returns the outcome for the final (done) message.</summary>
    private UpdateOutcome ApplyUpgradeProgress(string label, UpgradeRow summary, WingetProgress m, bool linux)
    {
        var isSummary = m.PackageId.Length == 0 || m.PackageId == LinuxUpgradeTracker.SystemId;
        if (!string.IsNullOrEmpty(m.LogLine))
            DeployLog(label, isSummary ? null : m.PackageId, m.LogLine);

        var row = isSummary ? summary : UpgradeRowFor(label, m.PackageId);
        switch (m.Phase)
        {
            case WingetPhase.Queued:
                if (row.IsFinished) break;
                row.DeployStatus = DeployItemStatus.Queued;
                if (isSummary) row.Stage = "Checking";
                break;
            case WingetPhase.Downloading:
                if (row.IsFinished) break;
                row.DeployStatus = DeployItemStatus.Downloading;
                row.Percent = m.Percent;
                row.Stage = isSummary ? (linux ? "Refreshing" : "Checking") : null;
                break;
            case WingetPhase.Installing:
                if (row.IsFinished) break;
                row.DeployStatus = DeployItemStatus.Installing;
                row.Percent = isSummary ? summary.Percent : m.Percent;
                row.Stage = isSummary ? "Upgrading" : null;
                break;
            default:
                if (isSummary && !m.Done) break;
                var status = m.Phase switch
                {
                    WingetPhase.Succeeded => DeployItemStatus.Success,
                    WingetPhase.Skipped => DeployItemStatus.Skipped,
                    _ => DeployItemStatus.Failed,
                };
                var detail = FormatUpgradeExit(m, linux);
                FinishRow(row, status, detail);
                DeployLog(label, isSummary ? null : m.PackageId, $"{(isSummary ? "Update All" : m.Phase.ToString())}: {detail}");
                break;
        }

        if (!isSummary && summary.DeployStatus == DeployItemStatus.Installing)
        {
            var rows = _upgradeRows.Where(r => r.Endpoint == label && !r.IsSummary).ToList();
            if (rows.Count > 0) summary.Percent = rows.Count(r => r.IsFinished) * 100 / rows.Count;
        }

        return m.Phase switch
        {
            WingetPhase.Succeeded => UpdateOutcome.Upgraded,
            WingetPhase.Skipped => UpdateOutcome.UpToDate,
            _ => UpdateOutcome.Failed,
        };
    }

    private static string FormatUpgradeExit(WingetProgress m, bool linux)
    {
        var msg = string.IsNullOrWhiteSpace(m.ExitMessage) ? (linux ? $"exit code {m.ExitCode}" : WingetRules.Describe(m.ExitCode)) : m.ExitMessage;
        if (m.ExitCode == 0) return msg;
        return linux ? $"exit {m.ExitCode}: {msg}" : DescribeExit(m.ExitCode, msg);
    }

    private static void FinishRow(UpgradeRow row, DeployItemStatus status, string detail)
    {
        row.DeployStatus = status;
        row.Percent = -1;
        row.Stage = null;
        row.StatusDetail = detail;
    }

    /// <summary>The row for (endpoint, package), created after that endpoint's other rows when missing.</summary>
    private UpgradeRow UpgradeRowFor(string endpoint, string package)
    {
        var existing = _upgradeRows.FirstOrDefault(r => r.Endpoint == endpoint && string.Equals(r.Package, package, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var row = new UpgradeRow { Endpoint = endpoint, Package = package };
        var last = -1;
        for (var i = 0; i < _upgradeRows.Count; i++)
            if (_upgradeRows[i].Endpoint == endpoint) last = i;
        if (last < 0) _upgradeRows.Add(row);
        else _upgradeRows.Insert(last + 1, row);
        return row;
    }

    /// <summary>Screenshot mode: sample Update All rows (nothing is run).</summary>
    private void ShowUpdateAllDemoRows()
    {
        _upgradeRows.Clear();
        ShowUpgradeQueue(true);
        var pc = UpgradeRowFor("OFFICE-PC", "");
        pc.DeployStatus = DeployItemStatus.Installing;
        pc.Stage = "Upgrading";
        pc.Percent = 50;
        FinishRow(UpgradeRowFor("OFFICE-PC", "Git.Git"), DeployItemStatus.Success, "Upgraded");
        var edge = UpgradeRowFor("OFFICE-PC", "Microsoft.Edge");
        edge.DeployStatus = DeployItemStatus.Downloading;
        edge.Percent = 42;
        UpgradeRowFor("OFFICE-PC", "7zip.7zip");
        FinishRow(UpgradeRowFor("OFFICE-PC", "Zoom.Zoom"), DeployItemStatus.Failed, "exit 1603 (0x00000643): Installer failed with exit code: 1603");
        var nas = UpgradeRowFor("ubuntu-server", "");
        nas.DeployStatus = DeployItemStatus.Downloading;
        nas.Stage = "Refreshing";
        FinishRow(UpgradeRowFor("garage-nas", ""), DeployItemStatus.Skipped,
            "Skipped: SSH appliances are not supported by Update All (install the Home RMS Linux agent on it).");
    }
}
