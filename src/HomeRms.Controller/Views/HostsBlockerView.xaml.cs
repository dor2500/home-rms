using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Controller.Themes;
using HomeRms.Shared.Hosts;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Views;

public partial class HostsBlockerView : UserControl
{
    private sealed class PushTarget : INotifyPropertyChanged
    {
        private bool _isChecked;
        public required MachineProfile Profile { get; init; }
        public string Label => $"{Profile.DisplayTitle} ({(Profile.IsConnected ? "connected" : Profile.IsOnline ? "online" : "offline")})";
        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private FeatureContext? _ctx;
    private readonly ObservableCollection<string> _domains = new();
    private readonly ObservableCollection<PushTarget> _targets = new();
    private string _loadedHash = "";
    private bool _busy;

    public HostsBlockerView()
    {
        InitializeComponent();
        DomainsList.ItemsSource = _domains;
        TargetsList.ItemsSource = _targets;
    }

    public void Initialize(FeatureContext ctx)
    {
        _ctx = ctx;
        RefreshEndpointChoices();
    }

    public void OnShown()
    {
        RefreshEndpointChoices();
        if (EndpointCombo.SelectedItem is MachineProfile && string.IsNullOrEmpty(_loadedHash))
            _ = LoadConfigAsync();
    }

    private void RefreshEndpointChoices()
    {
        if (_ctx is null) return;
        var prev = EndpointCombo.SelectedItem as MachineProfile;
        var agents = _ctx.AgentEndpoints.ToList();
        EndpointCombo.ItemsSource = agents;

        var focused = _ctx.FocusedEndpoint();
        if (prev is not null && agents.Contains(prev))
            EndpointCombo.SelectedItem = prev;
        else if (focused is not null && agents.Contains(focused))
            EndpointCombo.SelectedItem = focused;
        else if (agents.Count > 0)
            EndpointCombo.SelectedIndex = 0;

        var checkedSet = _targets.Where(t => t.IsChecked).Select(t => t.Profile).ToHashSet();
        _targets.Clear();
        foreach (var a in agents)
            _targets.Add(new PushTarget { Profile = a, IsChecked = checkedSet.Contains(a) });
    }

    private MachineProfile? CurrentTarget => EndpointCombo.SelectedItem as MachineProfile;

    private void OnEndpointComboChanged(object sender, SelectionChangedEventArgs e)
    {
        _loadedHash = "";
    }

    private void OnLoadClick(object sender, RoutedEventArgs e) => _ = LoadConfigAsync();

    private async Task LoadConfigAsync()
    {
        if (_ctx is null || CurrentTarget is not { } m || _busy) return;
        SetBusy(true, $"Loading hosts configuration from {m.DisplayTitle}...");
        try
        {
            var cfg = await _ctx.Pool.CallAsync(m, interactive: true,
                c => c.GetHostsConfigAsync(new Empty()).ResponseAsync);
            ApplyConfig(cfg);
            StatusText.Text = $"Loaded {cfg.Path} from {m.DisplayTitle}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Load failed: {AgentErrors.Describe(ex)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyConfig(HostsConfig cfg)
    {
        _loadedHash = cfg.RawHash ?? "";
        HostsPathText.Text = $"Path: {cfg.Path} ({cfg.OsFamily})";
        EncodingText.Text = $"{cfg.Encoding} · {cfg.Newline}";

        ElevationText.Text = cfg.IsElevated ? "Elevated (admin/root)" : "Not elevated (read-only)";
        ElevationBadge.Background = cfg.IsElevated ? Palette.Soft(PaletteTone.Success) : Palette.Soft(PaletteTone.Warning);
        ElevationText.Foreground = cfg.IsElevated ? Palette.Fg(PaletteTone.Success) : Palette.Fg(PaletteTone.Warning);

        ListenerEnabledCheck.IsChecked = cfg.ListenerEnabled;
        if (!string.IsNullOrWhiteSpace(cfg.LoopbackIp))
            LoopbackIpBox.Text = cfg.LoopbackIp;

        ListenerSummaryText.Text = cfg.Listener?.Summary is { Length: > 0 } s
            ? s
            : "Listener off (blocked domains map to 0.0.0.0; ports 80/443 are not bound).";
        if (cfg.Listener?.Warnings.Count > 0)
            ListenerSummaryText.Text += "  ⚠ " + string.Join(" ", cfg.Listener.Warnings);

        BackupPathText.Text = string.IsNullOrWhiteSpace(cfg.LastBackupPath) ? "" : $"Last backup: {cfg.LastBackupPath}";

        _domains.Clear();
        foreach (var d in cfg.Domains) _domains.Add(d);
        IncludeWwwCheck.IsChecked = cfg.IncludeWww;
        RawHostsBox.Text = cfg.RawText ?? "";

        if (!string.IsNullOrWhiteSpace(cfg.BlockError))
            StatusText.Text = $"⚠ Managed block warning: {cfg.BlockError}";
    }

    private void OnNewDomainKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnAddDomainClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnAddDomainClick(object sender, RoutedEventArgs e)
    {
        var raw = NewDomainBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;
        var tokens = raw.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var (normalized, errors) = HostsFile.NormalizeDomains(tokens);
        if (errors.Count > 0)
        {
            StatusText.Text = string.Join(" ", errors);
            return;
        }
        var added = 0;
        foreach (var d in normalized)
        {
            if (!_domains.Contains(d, StringComparer.OrdinalIgnoreCase))
            {
                _domains.Add(d);
                added++;
            }
        }
        NewDomainBox.Text = "";
        StatusText.Text = added > 0 ? $"Added {added} domain(s) to the list (click Save blocklist to apply)." : "Domain(s) already in the list.";
    }

    private void OnRemoveDomainClick(object sender, RoutedEventArgs e)
    {
        var selected = DomainsList.SelectedItems.Cast<string>().ToList();
        if (selected.Count == 0) return;
        foreach (var s in selected) _domains.Remove(s);
        StatusText.Text = $"Removed {selected.Count} domain(s) (click Save blocklist to apply).";
    }

    private void OnDryRunBlocklistClick(object sender, RoutedEventArgs e) => _ = SaveBlocklistAsync(dryRun: true);
    private void OnSaveBlocklistClick(object sender, RoutedEventArgs e) => _ = SaveBlocklistAsync(dryRun: false);

    private async Task SaveBlocklistAsync(bool dryRun)
    {
        if (_ctx is null || CurrentTarget is not { } m || _busy) return;
        SetBusy(true, dryRun ? "Checking blocklist changes..." : $"Saving blocklist to {m.DisplayTitle}...");
        try
        {
            var req = new HostsBlocklistRequest
            {
                IncludeWww = IncludeWwwCheck.IsChecked == true,
                DryRun = dryRun,
            };
            req.Domains.AddRange(_domains);
            var res = await _ctx.Pool.CallAsync(m, interactive: true,
                c => c.SetHostsBlocklistAsync(req).ResponseAsync);
            if (res.Config is not null) ApplyConfig(res.Config);
            StatusText.Text = FormatResult(res);
            if (!dryRun && res.Success)
                _ctx.Log("HOSTS", $"[{m.DisplayTitle}] {res.Message}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {AgentErrors.Describe(ex)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnApplyListenerClick(object sender, RoutedEventArgs e)
    {
        if (_ctx is null || CurrentTarget is not { } m || _busy) return;
        var enabled = ListenerEnabledCheck.IsChecked == true;
        var ip = LoopbackIpBox.Text.Trim();
        SetBusy(true, $"{(enabled ? "Enabling" : "Disabling")} blocked-access listener on {m.DisplayTitle}...");
        try
        {
            var res = await _ctx.Pool.CallAsync(m, interactive: true,
                c => c.SetBlockListenerAsync(new BlockListenerRequest { Enabled = enabled, LoopbackIp = ip }).ResponseAsync);
            if (res.Config is not null) ApplyConfig(res.Config);
            StatusText.Text = FormatResult(res);
            _ctx.Log("HOSTS", $"[{m.DisplayTitle}] Listener {(enabled ? "enabled" : "disabled")}: {res.Message}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Listener update failed: {AgentErrors.Describe(ex)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnValidateRawClick(object sender, RoutedEventArgs e)
    {
        if (_ctx is null || CurrentTarget is not { } m || _busy) return;
        SetBusy(true, "Validating hosts file syntax...");
        try
        {
            var res = await _ctx.Pool.CallAsync(m, interactive: true,
                c => c.SetHostsRawAsync(new HostsRawRequest
                {
                    Text = RawHostsBox.Text,
                    ExpectedHash = _loadedHash,
                    ValidateOnly = true,
                }).ResponseAsync);
            StatusText.Text = FormatResult(res);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Validation failed: {AgentErrors.Describe(ex)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSaveRawClick(object sender, RoutedEventArgs e)
    {
        if (_ctx is null || CurrentTarget is not { } m || _busy) return;
        SetBusy(true, $"Saving raw hosts file on {m.DisplayTitle}...");
        try
        {
            var res = await _ctx.Pool.CallAsync(m, interactive: true,
                c => c.SetHostsRawAsync(new HostsRawRequest
                {
                    Text = RawHostsBox.Text,
                    ExpectedHash = _loadedHash,
                    ValidateOnly = false,
                }).ResponseAsync);
            if (res.Config is not null) ApplyConfig(res.Config);
            StatusText.Text = FormatResult(res);
            if (res.Success) _ctx.Log("HOSTS", $"[{m.DisplayTitle}] {res.Message}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save raw failed: {AgentErrors.Describe(ex)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPushToTargetsClick(object sender, RoutedEventArgs e)
    {
        if (_ctx is null || _busy) return;
        var checkedTargets = _targets.Where(t => t.IsChecked).Select(t => t.Profile).ToList();
        if (checkedTargets.Count == 0)
        {
            StatusText.Text = "Check one or more endpoints in the Multi-endpoint push list first.";
            return;
        }

        var includeWww = IncludeWwwCheck.IsChecked == true;
        var pushListener = PushListenerTooCheck.IsChecked == true;
        var listenerOn = ListenerEnabledCheck.IsChecked == true;
        var loopback = LoopbackIpBox.Text.Trim();
        var domains = _domains.ToList();

        SetBusy(true, $"Pushing blocklist ({domains.Count} domains) to {checkedTargets.Count} endpoint(s)...");
        var ok = 0;
        var fail = 0;
        foreach (var target in checkedTargets)
        {
            try
            {
                var req = new HostsBlocklistRequest { IncludeWww = includeWww };
                req.Domains.AddRange(domains);
                var res = await _ctx.Pool.CallAsync(target, interactive: true,
                    c => c.SetHostsBlocklistAsync(req).ResponseAsync);
                if (!res.Success)
                {
                    fail++;
                    _ctx.Log("ALERT", $"[{target.DisplayTitle}] Hosts push failed: {res.Message}");
                    continue;
                }
                if (pushListener)
                {
                    await _ctx.Pool.CallAsync(target, interactive: true,
                        c => c.SetBlockListenerAsync(new BlockListenerRequest { Enabled = listenerOn, LoopbackIp = loopback }).ResponseAsync);
                }
                ok++;
                _ctx.Log("HOSTS", $"[{target.DisplayTitle}] {res.Message}");
            }
            catch (Exception ex)
            {
                fail++;
                _ctx.Log("ALERT", $"[{target.DisplayTitle}] Hosts push failed: {AgentErrors.Describe(ex)}");
            }
        }
        StatusText.Text = $"Multi-endpoint push finished: {ok} succeeded, {fail} failed.";
        SetBusy(false);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        SaveBlocklistBtn.IsEnabled = !busy;
        SaveRawBtn.IsEnabled = !busy;
        ApplyListenerBtn.IsEnabled = !busy;
        PushMultiBtn.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }

    private static string FormatResult(HostsOpResult res)
    {
        var parts = new List<string> { res.Message };
        if (res.Errors.Count > 0) parts.Add("Errors: " + string.Join("; ", res.Errors));
        if (res.Steps.Count > 0) parts.Add(string.Join(" | ", res.Steps));
        return string.Join("  —  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
