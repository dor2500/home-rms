using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Shared;
using HomeRms.Shared.Network;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

namespace HomeRms.Controller;

/// <summary>
/// Connect-by-computer-name support: endpoints are found again after their IP changes (LAN discovery at
/// startup / every minute / on Scan LAN, re-resolve on connection failure, signed agent announces from
/// other subnets) and the agent's computer name, address, certificate and MAC are recorded on contact.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan HealInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RelocateCooldown = TimeSpan.FromSeconds(60);

    private DispatcherTimer? _healTimer;
    private bool _scanRunning;
    private bool _healRunning;
    private AnnounceListener? _announceListener;
    private readonly Dictionary<MachineProfile, Task<bool>> _relocations = new();
    private readonly Dictionary<MachineProfile, DateTime> _lastRelocateUtc = new();
    private readonly ConcurrentDictionary<string, X509Certificate2> _knownAgentCerts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _announceSuggestions = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsScreenshotRun =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--screenshot-dir", StringComparison.OrdinalIgnoreCase));

    /// <summary>Called from the constructor after <see cref="InitializeFeatureViews"/> (the pool exists).</summary>
    private void InitializeEndpointLocator()
    {
        // Screenshot runs use sample endpoints: never go looking for them on the network.
        if (!IsScreenshotRun)
            _pool.Relocate = (m, ct) => Dispatcher.InvokeAsync(() => RelocateProfileAsync(m, ct)).Task.Unwrap();
        _pool.Paired = (m, conn, info) => Dispatcher.BeginInvoke(() =>
        {
            if (RecordAgentContact(m, conn, info)) MachineStore.Save(_machines);
        });

        foreach (var m in _machines) RememberAgentCert(m);
        if (IsScreenshotRun) return; // screenshots: no background discovery, no listener, nothing persisted

        StartAnnounceListener();
        _healTimer = new DispatcherTimer { Interval = HealInterval };
        _healTimer.Tick += async (_, _) => await BackgroundHealAsync();
        _healTimer.Start();
        Closed += (_, _) =>
        {
            _healTimer?.Stop();
            _announceListener?.Dispose();
            MachineStore.Save(_machines); // keeps "last seen" from the telemetry stream
        };
    }

    // ---------------- Healing ----------------

    /// <summary>Discovery + heal; with <paramref name="addNew"/> (Scan LAN) also adds agents that are not saved yet.</summary>
    private async Task<HealReport?> RunHealAsync(bool addNew, CancellationToken ct = default)
    {
        if (_healRunning) return null;
        _healRunning = true;
        try
        {
            var report = await EndpointLocator.HealAsync(_machines, () => MachineStore.Save(_machines), AddActivity, ct);
            AfterHeal(report);
            if (addNew) await AddDiscoveredAgentsAsync(report.Unmatched, ct);
            return report;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            AddActivity("LAN SCAN", $"Endpoint discovery failed: {ex.Message}");
            return null;
        }
        finally
        {
            _healRunning = false;
        }
    }

    private async Task BackgroundHealAsync()
    {
        if (_scanRunning || _healRunning) return;
        var report = await RunHealAsync(addNew: false);
        if (report is null) return;
        // Endpoints that just moved were probably shown offline: probe them at their new address.
        foreach (var change in report.Changes.Where(c => c.Plan.MovesHost && !c.Profile.IsConnected))
            await ProbeOneAsync(change.Profile);
        if (report.Changes.Count > 0) UpdateOverviewMetrics();
    }

    /// <summary>
    /// Periodic LAN scan for alerts: discovers agents on the network, saves new ones,
    /// probes them, and connects (non-interactively) so real-time alerts stream automatically.
    /// </summary>
    private async Task AutoScanAndConnectForAlertsAsync()
    {
        if (_scanRunning || _healRunning || IsConsoleLocked) return;
        var before = _machines.ToHashSet();
        var report = await RunHealAsync(addNew: true);
        if (report is null) return;

        var newlyAdded = _machines.Where(m => !before.Contains(m)).ToList();
        var moved = report.Changes.Where(c => c.Plan.MovesHost && !c.Profile.IsConnected).Select(c => c.Profile);
        foreach (var m in newlyAdded.Concat(moved).Distinct())
            await ProbeOneAsync(m);

        if (_alerts.Thresholds.AutoConnectForAlerts)
        {
            foreach (var m in _machines.Where(m => m.IsAgentEndpoint && (m.IsOnline || m.IsConnected) && _pool.GetCachedInfo(m) is null).ToList())
            {
                try { await _pool.GetPairedAsync(m, interactive: false); }
                catch { /* unpaired (2FA required) or offline; skip quietly */ }
            }
        }

        _telemetry.Sync(_machines);
        _eventWatcher.Sync(_machines);
        if (newlyAdded.Count > 0 || report.Changes.Count > 0) UpdateOverviewMetrics();
    }

    /// <summary>Drops pooled connections that still point at an endpoint's old address.</summary>
    private void AfterHeal(HealReport report)
    {
        foreach (var c in report.Changes)
        {
            RememberAgentCert(c.Profile);
            if (string.Equals(c.OldDialHost, c.Profile.DialHost, StringComparison.OrdinalIgnoreCase) && c.OldPort == c.Profile.Port) continue;
            _pool.Evict(c.OldDialHost, c.OldPort);
            _centralHub.EvictConnection(c.OldDialHost, c.OldPort);
        }
    }

    private async Task AddDiscoveredAgentsAsync(IReadOnlyList<AgentSighting> unmatched, CancellationToken ct)
    {
        var added = 0;
        foreach (var s in unmatched.Where(s => s.CertThumbprint is null && s.Addresses.Count > 0))
        {
            if (_machines.Any(m => EndpointAddress.NamesEqual(m.ComputerName, s.MachineName) ||
                                   (!m.ConnectsByIp && EndpointAddress.NamesEqual(m.Host, s.MachineName))))
                continue;
            var ip = s.Addresses[0];
            // Prefer the computer name when this PC can resolve it to the agent: it survives IP changes.
            var byName = EndpointAddress.IsValidHost(s.MachineName, out _) && await EndpointLocator.NameResolvesToAsync(s.MachineName, ip, ct);
            _machines.Add(new MachineProfile
            {
                Name = s.MachineName,
                Host = byName ? s.MachineName : ip.ToString(),
                Port = s.Port,
                ComputerName = s.MachineName,
                LastKnownIp = ip.ToString(),
                KnownAddresses = s.Addresses.Select(a => a.ToString()).ToList(),
                StatusLabel = "Discovered",
            });
            added++;
            AddActivity("LAN SCAN", $"Discovered agent {s.MachineName} at {ip}:{s.Port} (saved by {(byName ? "computer name" : "IP address")}).");
        }
        if (added > 0) MachineStore.Save(_machines);
    }

    /// <summary>
    /// Re-resolves an endpoint after a transport failure (at most once a minute per endpoint, one run at a
    /// time). Endpoints that connect by IP get the new IP saved; endpoints that connect by name dial the
    /// found address for this session. Returns true when the dial address changed (worth one retry).
    /// </summary>
    private async Task<bool> RelocateProfileAsync(MachineProfile m, CancellationToken ct)
    {
        if (_relocations.TryGetValue(m, out var running)) return await running;
        var now = DateTime.UtcNow;
        if (_lastRelocateUtc.TryGetValue(m, out var last) && now - last < RelocateCooldown) return false;
        _lastRelocateUtc[m] = now;

        var task = RelocateCoreAsync(m, ct);
        _relocations[m] = task;
        try { return await task; }
        finally { _relocations.Remove(m); }
    }

    private async Task<bool> RelocateCoreAsync(MachineProfile m, CancellationToken ct)
    {
        if (!EndpointHealing.IsAgentEndpoint(m) || EndpointAddress.IsLoopbackHost(m.Host)) return false;
        var oldDial = m.DialHost;
        var oldPort = m.Port;
        EndpointResolution? r;
        try { r = await EndpointLocator.ResolveAsync(m, ct); }
        catch (OperationCanceledException) { return false; }
        if (r is null)
        {
            AddActivity("FLEET", $"{m.DisplayTitle} is not answering at {oldDial}:{oldPort} and was not found at another address.");
            return false;
        }

        if (m.ConnectsByIp)
        {
            if (!string.Equals(r.Address, m.Host, StringComparison.OrdinalIgnoreCase) || r.Port != m.Port)
            {
                var from = $"{m.Host}:{m.Port}";
                m.Host = r.Address;
                m.Port = r.Port;
                AddActivity("FLEET", $"{m.DisplayTitle} moved {from} → {m.Host}:{m.Port} (found by {r.Via}).");
            }
        }
        else
        {
            m.DialOverride = string.Equals(r.Address, m.Host, StringComparison.OrdinalIgnoreCase) ? null : r.Address;
            m.Port = r.Port;
        }
        if (EndpointAddress.IsIpAddress(r.Address) && !EndpointAddress.IsLoopbackHost(r.Address)) m.LastKnownIp = r.Address;

        var changed = !string.Equals(m.DialHost, oldDial, StringComparison.OrdinalIgnoreCase) || m.Port != oldPort;
        if (changed)
        {
            _pool.Evict(oldDial, oldPort);
            _centralHub.EvictConnection(oldDial, oldPort);
        }
        MachineStore.Save(_machines);
        return changed;
    }

    // ---------------- Contact recording ----------------

    /// <summary>
    /// Records what a successful mTLS contact tells us: the agent's computer name, the IP actually reached,
    /// the agent certificate (announces must match it) and "last seen". Fills a missing MAC from ARP in the
    /// background. Returns true when a persisted field changed (caller saves).
    /// </summary>
    private bool RecordAgentContact(MachineProfile m, RmsConnection conn, AgentInfo info)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(info.MachineName) && !string.Equals(m.ComputerName, info.MachineName, StringComparison.Ordinal))
        {
            m.ComputerName = info.MachineName;
            changed = true;
        }
        if (conn.RemoteAddress is { } ip && !IPAddress.IsLoopback(ip))
        {
            var text = ip.ToString();
            if (!string.Equals(m.LastKnownIp, text, StringComparison.OrdinalIgnoreCase)) { m.LastKnownIp = text; changed = true; }
        }
        if (conn.ServerCertificate is { } cert)
        {
            var thumb = AgentAnnounce.Thumbprint(cert);
            if (!string.Equals(m.AgentCertThumbprint, thumb, StringComparison.OrdinalIgnoreCase))
            {
                m.AgentCertThumbprint = thumb;
                m.AgentCertificate = Convert.ToBase64String(cert.RawData);
                changed = true;
            }
            _knownAgentCerts[thumb] = cert;
        }
        m.LastSeenUtc = DateTime.UtcNow;

        if (PowerActions.NormalizeMac(m.MacAddress) is null && IPAddress.TryParse(m.LastKnownIp, out var lastIp))
            _ = FillMacFromArpAsync(m, lastIp);
        return changed;
    }

    private async Task FillMacFromArpAsync(MachineProfile m, IPAddress ip)
    {
        try
        {
            var mac = await Task.Run(() => ArpLookup.TryGetMac(ip, EndpointLocator.GetLocalSubnets()));
            if (mac is null || PowerActions.NormalizeMac(m.MacAddress) is not null) return;
            m.MacAddress = mac;
            if (!IsScreenshotRun) MachineStore.Save(_machines);
            AddActivity("WAKE", $"Learned MAC address {mac} of {m.DisplayTitle} from ARP (Wake-on-LAN works after IP changes).");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"ARP lookup for {ip} failed: {ex.Message}");
        }
    }

    private void RememberAgentCert(MachineProfile m)
    {
        if (string.IsNullOrWhiteSpace(m.AgentCertificate) || string.IsNullOrWhiteSpace(m.AgentCertThumbprint)) return;
        if (_knownAgentCerts.ContainsKey(m.AgentCertThumbprint)) return;
        try
        {
            var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(m.AgentCertificate));
            if (string.Equals(AgentAnnounce.Thumbprint(cert), m.AgentCertThumbprint, StringComparison.OrdinalIgnoreCase))
                _knownAgentCerts[m.AgentCertThumbprint] = cert;
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            Debug.WriteLine($"Stored agent certificate of {m.DisplayTitle} is unreadable: {ex.Message}");
        }
    }

    // ---------------- Add / Edit endpoint drawer ----------------

    /// <summary>The profile the drawer edits (null while adding a new endpoint).</summary>
    private MachineProfile? _editingProfile;
    private bool _settingConnectUsing;

    /// <summary>Prepares the drawer for <paramref name="m"/>: the "Connect using" choice appears when its computer name is known.</summary>
    private void SetupConnectUsing(MachineProfile? m)
    {
        _editingProfile = m;
        HostErrorText.Visibility = Visibility.Collapsed;
        var show = m is not null && !string.IsNullOrWhiteSpace(m.ComputerName) && !EndpointAddress.IsLoopbackHost(m.Host)
                   && EndpointHealing.IsAgentEndpoint(m);
        ConnectUsingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ConnectUsingSpacer.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) return;

        var ip = DrawerIpChoice(m!);
        _settingConnectUsing = true;
        ConnectByNameDetail.Text = $"  {m!.ComputerName}";
        ConnectByIpDetail.Text = string.IsNullOrEmpty(ip) ? "  (not known yet)" : $"  {ip}";
        ConnectByIpRadio.IsEnabled = !string.IsNullOrEmpty(ip);
        ConnectByNameRadio.IsChecked = !m.ConnectsByIp;
        ConnectByIpRadio.IsChecked = m.ConnectsByIp;
        _settingConnectUsing = false;
        UpdateConnectUsingHint();
    }

    private static string DrawerIpChoice(MachineProfile m) =>
        m.ConnectsByIp ? m.Host : EndpointAddress.IsIpAddress(m.LastKnownIp) ? m.LastKnownIp : "";

    private void OnConnectUsingChecked(object sender, RoutedEventArgs e)
    {
        if (_settingConnectUsing || _editingProfile is not { } m) return;
        if (ConnectByNameRadio.IsChecked == true) HostBox.Text = m.ComputerName;
        else if (ConnectByIpRadio.IsChecked == true && DrawerIpChoice(m) is { Length: > 0 } ip) HostBox.Text = ip;
        UpdateConnectUsingHint();
    }

    private void UpdateConnectUsingHint()
    {
        ConnectUsingHint.Text = ConnectByNameRadio.IsChecked == true
            ? "Looked up on every connection (DNS / LLMNR / NetBIOS / mDNS), so IP changes do not matter."
            : "If the IP changes, Home RMS finds this computer again by its name and updates the IP automatically.";
    }

    private void OnHostBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (HostErrorText is not null) HostErrorText.Visibility = Visibility.Collapsed;
        if (_settingConnectUsing || _editingProfile is not { } m || ConnectUsingPanel?.Visibility != Visibility.Visible) return;
        var text = HostBox.Text.Trim();
        _settingConnectUsing = true;
        ConnectByNameRadio.IsChecked = EndpointAddress.NamesEqual(text, m.ComputerName) && !EndpointAddress.IsIpAddress(text);
        ConnectByIpRadio.IsChecked = EndpointAddress.IsIpAddress(text);
        _settingConnectUsing = false;
    }

    /// <summary>Shows an inline error and returns false when the host or port is not valid.</summary>
    private bool ValidateDrawerAddress(MachineProfile draft)
    {
        string? error = null;
        if (!EndpointAddress.IsValidHost(draft.Host, out var hostError)) error = hostError;
        else if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535) error = "Port must be a number from 1 to 65535.";
        HostErrorText.Text = error ?? "";
        HostErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        if (error is not null) HostBox.Focus();
        return error is null;
    }

    // ---------------- Signed agent announces ----------------

    private void StartAnnounceListener()
    {
        try
        {
            var ca = DevCertificates.LoadCaPublicOnly(RmsPaths.CertDir, RmsPaths.CertPassword);
            try
            {
                // The deployment agent certificate is a valid signer too (public part only).
                var deployment = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.AgentFileName, RmsPaths.CertPassword);
                var pub = X509CertificateLoader.LoadCertificate(deployment.RawData);
                _knownAgentCerts.TryAdd(AgentAnnounce.Thumbprint(pub), pub);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
            {
                Debug.WriteLine("Deployment agent certificate unavailable: " + ex.Message);
            }

            var verifier = new AnnounceVerifier(ca, thumb => _knownAgentCerts.TryGetValue(thumb, out var c) ? c : null);
            var listener = new AnnounceListener(RmsNetwork.AnnounceUdpPort, verifier, msg => Debug.WriteLine("[announce] " + msg));
            listener.Verified += (result, from) => Dispatcher.BeginInvoke(() => OnAnnounceVerified(result, from));
            if (!listener.Start(out var error))
            {
                listener.Dispose();
                AddActivity("LAN SCAN", $"Agent announces (UDP {RmsNetwork.AnnounceUdpPort}) are unavailable: {error}. Endpoints on other subnets are not followed automatically.");
                return;
            }
            _announceListener = listener;
            EnsureAnnounceFirewallRule();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            AddActivity("LAN SCAN", $"Agent announce listener not started: {ex.Message}");
        }
    }

    private void OnAnnounceVerified(AnnounceResult result, IPEndPoint from)
    {
        var p = result.Payload!;
        var addresses = EndpointAddress.Rank([from.Address, .. p.Addresses], EndpointLocator.GetLocalSubnets());
        var sighting = new AgentSighting(p.MachineName, p.Port, addresses, "signed agent announce", p.Thumbprint);
        if (result.Certificate is { } cert) _knownAgentCerts.TryAdd(p.Thumbprint, cert);

        var report = EndpointLocator.Heal(_machines, [sighting], () => MachineStore.Save(_machines), AddActivity);
        AfterHeal(report);
        foreach (var change in report.Changes.Where(c => c.Plan.MovesHost && !c.Profile.IsConnected))
            _ = ProbeOneAsync(change.Profile);
        if (report.Unmatched.Count == 0) return;

        var known = _machines.FirstOrDefault(m => EndpointAddress.NamesEqual(m.ComputerName, p.MachineName));
        if (known is not null)
        {
            // Same name, but the certificate was never confirmed on a TLS connection: never redirect on that.
            Debug.WriteLine($"[announce] {p.MachineName}: certificate {p.Thumbprint[..12]}… does not match the one pinned for '{known.DisplayTitle}'; ignored.");
            return;
        }
        if (_announceSuggestions.Add(p.MachineName))
            AddActivity("FLEET", $"Agent {p.MachineName} announced itself from {from.Address} ({string.Join(", ", addresses.Take(3))}) but is not a saved endpoint. Add it with Add endpoint if you want to manage it.");
    }

    /// <summary>Best effort, elevated runs only: allow inbound announces on private / domain networks.</summary>
    private static void EnsureAnnounceFirewallRule()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
            var rule = $"Home RMS Controller (Agent announce UDP {RmsNetwork.AnnounceUdpPort})";
            if (RunNetsh($"advfirewall firewall show rule name=\"{rule}\"") == 0) return;
            var exe = Environment.ProcessPath ?? "";
            RunNetsh($"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=UDP localport={RmsNetwork.AnnounceUdpPort} program=\"{exe}\" profile=private,domain");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogBus.Publish(LogBus.Activity, "LAN SCAN", $"Could not add the firewall rule for agent announces: {ex.Message}", Themes.PaletteTone.Warning);
        }
    }

    private static int RunNetsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return p.HasExited ? p.ExitCode : -1;
    }
}
