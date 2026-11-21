using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;
using HomeRms.Controller.Themes;

namespace HomeRms.Controller.Models;

public enum EndpointKind
{
    WindowsAgent = 0,
    WindowsAgentless = 1,
    HomeAssistant = 2,
    Nas = 3,
    Linux = 4,
    WebService = 5,
}

public enum ConnectionMode
{
    Agent = 0,
    Agentless = 1,
}

/// <summary>A saved endpoint (Windows Agent, Agentless WinRM/RDP, or Server/Appliance) managed by Home RMS Server.</summary>
public sealed class MachineProfile : INotifyPropertyChanged
{
    private string _name = "";
    private string _host = "";
    private int _port = 7443;
    private string _macAddress = "";
    private EndpointKind _kind = EndpointKind.WindowsAgent;
    private ConnectionMode _connectionMode = ConnectionMode.Agent;
    private string _username = "";
    private string _encryptedPassword = "";
    private int _sshPort = 22;
    private string _sshKeyPath = "";
    private string _webUrl = "";
    private bool _useRdpMultimon = true;
    private bool? _twoFactorOverride;
    private int _unreadAlerts;
    private bool _isOnline;
    private bool _isConnected;
    private string _statusLabel = "Unknown";
    private string _userAndOs = "";
    private string _osName = "Windows";
    private double _cpuPercent;
    private double _ramPercent;
    private int _latencyMs;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); }
    }

    public string Host
    {
        get => _host;
        set
        {
            if (!string.Equals(_host, value, StringComparison.OrdinalIgnoreCase)) _dialOverride = null;
            _host = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(Address)); OnPropertyChanged(nameof(Subtitle));
            OnAddressingChanged();
        }
    }

    public int Port
    {
        get => _port;
        set { _port = value; OnPropertyChanged(); OnPropertyChanged(nameof(Address)); OnPropertyChanged(nameof(Subtitle)); OnAddressingChanged(); }
    }

    private string _computerName = "";
    private string _lastKnownIp = "";
    private List<string> _knownAddresses = [];
    private string _agentCertThumbprint = "";
    private string _agentCertificate = "";
    private string? _dialOverride;

    /// <summary>The agent's own machine name (GetAgentInfo / discovery), used to find the endpoint after its IP changes.</summary>
    public string ComputerName
    {
        get => _computerName;
        set { _computerName = value ?? ""; OnPropertyChanged(); OnAddressingChanged(); }
    }

    /// <summary>The IP address the endpoint was last reached at (TLS connection, discovery reply or verified announce).</summary>
    public string LastKnownIp
    {
        get => _lastKnownIp;
        set { _lastKnownIp = value ?? ""; OnPropertyChanged(); OnAddressingChanged(); }
    }

    /// <summary>Every address the agent reported for itself (multi-NIC, VPN), tried when the primary address fails.</summary>
    public List<string> KnownAddresses
    {
        get => _knownAddresses;
        set { _knownAddresses = value ?? []; OnPropertyChanged(); }
    }

    /// <summary>SHA-256 (hex) of the agent TLS certificate seen on the last successful connection; announces must match it.</summary>
    public string AgentCertThumbprint
    {
        get => _agentCertThumbprint;
        set { _agentCertThumbprint = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>The agent TLS certificate (DER, Base64) matching <see cref="AgentCertThumbprint"/>, used to verify its announces.</summary>
    public string AgentCertificate
    {
        get => _agentCertificate;
        set { _agentCertificate = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>
    /// Session-only address to dial instead of <see cref="Host"/> when the endpoint connects by computer
    /// name and the name did not resolve but the agent was found elsewhere (cleared when Host changes).
    /// </summary>
    [JsonIgnore]
    public string? DialOverride
    {
        get => _dialOverride;
        set { _dialOverride = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); }
    }

    /// <summary>The address connections actually dial.</summary>
    [JsonIgnore]
    public string DialHost => _dialOverride ?? _host;

    [JsonIgnore]
    public bool ConnectsByIp => Services.EndpointAddress.IsIpAddress(_host);

    /// <summary>"laptop-test · 192.168.1.140:7443" (by name) or "192.168.1.140:7443 · LAPTOP-TEST" (by IP).</summary>
    [JsonIgnore]
    public string AddressLine
    {
        get
        {
            if (Services.EndpointAddress.IsLoopbackHost(_host)) return Address;
            if (ConnectsByIp)
                return string.IsNullOrWhiteSpace(_computerName) ? Address : $"{Address} · {_computerName}";
            return string.IsNullOrWhiteSpace(_lastKnownIp) || string.Equals(_lastKnownIp, _host, StringComparison.OrdinalIgnoreCase)
                ? Address
                : $"{_host} · {_lastKnownIp}:{_port}";
        }
    }

    /// <summary>The endpoint connects by IP but its computer name is known, so a changed IP is followed automatically.</summary>
    [JsonIgnore]
    public bool ShowIpAutoHint => ConnectsByIp && !Services.EndpointAddress.IsLoopbackHost(_host) && !string.IsNullOrWhiteSpace(_computerName);

    [JsonIgnore]
    public string AddressToolTip
    {
        get
        {
            if (Services.EndpointAddress.IsLoopbackHost(_host)) return "This computer.";
            if (ShowIpAutoHint)
                return $"Connects by IP address. When the IP changes, Home RMS finds {_computerName} again (LAN discovery, signed agent announces, other known addresses) and updates it automatically. Edit the endpoint to connect by computer name instead.";
            if (ConnectsByIp)
                return "Connects by IP address. Connect once so Home RMS learns the computer name and can follow IP changes.";
            return string.IsNullOrWhiteSpace(_lastKnownIp)
                ? $"Connects by computer name ({_host}); the IP address is looked up on every connection."
                : $"Connects by computer name ({_host}); last reached at {_lastKnownIp}.";
        }
    }

    private void OnAddressingChanged()
    {
        OnPropertyChanged(nameof(AddressLine));
        OnPropertyChanged(nameof(ShowIpAutoHint));
        OnPropertyChanged(nameof(AddressToolTip));
        OnPropertyChanged(nameof(ConnectsByIp));
    }

    public string MacAddress
    {
        get => _macAddress;
        set { _macAddress = value; OnPropertyChanged(); }
    }

    public EndpointKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            if (value == EndpointKind.WindowsAgentless) _connectionMode = ConnectionMode.Agentless;
            else if (value == EndpointKind.WindowsAgent) _connectionMode = ConnectionMode.Agent;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionMode));
            OnPropertyChanged(nameof(ModeBadge));
            OnPropertyChanged(nameof(CategoryLabel));
            OnPropertyChanged(nameof(IsWindows));
            OnPropertyChanged(nameof(IsAppliance));
            OnPropertyChanged(nameof(IsAgentless));
            OnPropertyChanged(nameof(OsGlyph));
            OnPropertyChanged(nameof(OsTag));
        }
    }

    public ConnectionMode ConnectionMode
    {
        get => _connectionMode;
        set
        {
            _connectionMode = value;
            if (_kind == EndpointKind.WindowsAgent && value == ConnectionMode.Agentless) _kind = EndpointKind.WindowsAgentless;
            else if (_kind == EndpointKind.WindowsAgentless && value == ConnectionMode.Agent) _kind = EndpointKind.WindowsAgent;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(ModeBadge));
            OnPropertyChanged(nameof(IsAgentless));
        }
    }

    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); }
    }

    /// <summary>DPAPI-encrypted password (CurrentUser scope, Base64).</summary>
    public string EncryptedPassword
    {
        get => _encryptedPassword;
        set { _encryptedPassword = value; OnPropertyChanged(); }
    }

    public int SshPort
    {
        get => _sshPort;
        set { _sshPort = value <= 0 ? 22 : value; OnPropertyChanged(); }
    }

    public string SshKeyPath
    {
        get => _sshKeyPath;
        set { _sshKeyPath = value; OnPropertyChanged(); }
    }

    public string WebUrl
    {
        get => _webUrl;
        set { _webUrl = value; OnPropertyChanged(); }
    }

    public bool UseRdpMultimon
    {
        get => _useRdpMultimon;
        set { _useRdpMultimon = value; OnPropertyChanged(); }
    }

    /// <summary>Optional per-endpoint 2FA override (null = inherit global setting).</summary>
    public bool? TwoFactorOverride
    {
        get => _twoFactorOverride;
        set { _twoFactorOverride = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public int UnreadAlerts
    {
        get => _unreadAlerts;
        set { _unreadAlerts = Math.Max(0, value); OnPropertyChanged(); }
    }

    [JsonIgnore]
    public bool IsWindows => _kind is EndpointKind.WindowsAgent or EndpointKind.WindowsAgentless;

    [JsonIgnore]
    public bool IsAppliance => _kind is EndpointKind.HomeAssistant or EndpointKind.Nas or EndpointKind.Linux or EndpointKind.WebService;

    [JsonIgnore]
    public bool IsAgentless => _connectionMode == ConnectionMode.Agentless && IsWindows;

    /// <summary>
    /// A Home RMS Linux agent (mTLS gRPC, bash shell): a Linux endpoint in agent mode, or an agent endpoint
    /// whose reported OS is Linux (e.g. added by LAN scan before its kind was known).
    /// </summary>
    [JsonIgnore]
    public bool IsLinuxAgent =>
        (_kind == EndpointKind.Linux && _connectionMode == ConnectionMode.Agent) ||
        (_kind == EndpointKind.WindowsAgent && ReportsLinux(_osName));

    /// <summary>Windows or Linux agent endpoint that answers the agent RPCs.</summary>
    [JsonIgnore]
    public bool IsAgentEndpoint => _kind == EndpointKind.WindowsAgent || IsLinuxAgent;

    /// <summary>A Windows agent endpoint (PowerShell shell, winget, Windows-only features).</summary>
    [JsonIgnore]
    public bool IsWindowsAgent => _kind == EndpointKind.WindowsAgent && !IsLinuxAgent;

    /// <summary>True when an agent OS string names Linux ("Linux · Ubuntu 24.04", older agents: "Unix 6.8.0").</summary>
    public static bool ReportsLinux(string? os) =>
        !string.IsNullOrEmpty(os) &&
        (os.Contains("Linux", StringComparison.OrdinalIgnoreCase) || os.StartsWith("Unix", StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public string ModeBadge => _kind switch
    {
        EndpointKind.WindowsAgent => "Agent: mTLS",
        EndpointKind.WindowsAgentless => "Agentless: WinRM / RDP",
        EndpointKind.HomeAssistant => "Home Assistant",
        EndpointKind.Nas => "NAS / Storage",
        EndpointKind.Linux => _connectionMode == ConnectionMode.Agent ? "Linux agent: mTLS" : "Linux / SSH",
        EndpointKind.WebService => "Web Service",
        _ => "Endpoint",
    };

    [JsonIgnore]
    public string CategoryLabel => _kind switch
    {
        EndpointKind.WindowsAgent or EndpointKind.WindowsAgentless => "WINDOWS",
        EndpointKind.HomeAssistant => "HOME ASSISTANT",
        EndpointKind.Nas => "NAS",
        EndpointKind.Linux => "LINUX",
        EndpointKind.WebService => "WEB SERVICE",
        _ => "OTHER",
    };

    public void SetPlainPassword(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            EncryptedPassword = "";
            return;
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
            bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        EncryptedPassword = Convert.ToBase64String(protectedBytes);
    }

    public string GetPlainPassword()
    {
        if (string.IsNullOrWhiteSpace(EncryptedPassword)) return "";
        try
        {
            var protectedBytes = Convert.FromBase64String(EncryptedPassword);
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                protectedBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    [JsonIgnore]
    public string Address => $"{Host}:{Port}";

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    [JsonIgnore]
    public string Subtitle => string.IsNullOrWhiteSpace(_userAndOs) ? Address : $"{Address} • {_userAndOs}";

    [JsonIgnore]
    public string UserAndOs
    {
        get => _userAndOs;
        set { _userAndOs = value; OnPropertyChanged(); OnPropertyChanged(nameof(Subtitle)); }
    }

    [JsonIgnore]
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            _isOnline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DotBrush));
            OnPropertyChanged(nameof(DotGlowColor));
            OnPropertyChanged(nameof(BadgeBackground));
            OnPropertyChanged(nameof(CpuLabel));
            OnPropertyChanged(nameof(RamLabel));
            OnPropertyChanged(nameof(LatencyLabel));
            OnPropertyChanged(nameof(HealthLine));
        }
    }

    [JsonIgnore]
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            _isConnected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DotBrush));
            OnPropertyChanged(nameof(DotGlowColor));
            OnPropertyChanged(nameof(BadgeBackground));
            OnPropertyChanged(nameof(ConnectActionText));
        }
    }

    [JsonIgnore]
    public string StatusLabel
    {
        get => _statusLabel;
        set { _statusLabel = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string OsName
    {
        get => _osName;
        set { _osName = value; OnPropertyChanged(); OnPropertyChanged(nameof(OsTag)); OnPropertyChanged(nameof(OsGlyph)); OnPropertyChanged(nameof(IsLinuxAgent)); }
    }

    [JsonIgnore]
    public string OsGlyph => _kind switch
    {
        EndpointKind.HomeAssistant => "HA",
        EndpointKind.Nas => "NAS",
        EndpointKind.Linux => "LNX",
        EndpointKind.WebService => "WEB",
        EndpointKind.WindowsAgentless => "RDP",
        _ => ReportsLinux(_osName) ? "LNX" : "WIN",
    };

    [JsonIgnore]
    public string OsTag
    {
        get
        {
            if (_kind == EndpointKind.HomeAssistant) return "Home Assistant";
            if (_kind == EndpointKind.Nas) return "NAS";
            if (_kind == EndpointKind.Linux) return "Linux";
            if (_kind == EndpointKind.WebService) return "Web UI";
            if (_kind == EndpointKind.WindowsAgentless) return "WinRM / RDP";
            if (_osName.Contains("WinFlex", StringComparison.OrdinalIgnoreCase)) return "WinFlexOS";
            if (ReportsLinux(_osName)) return "Linux";
            if (_osName.Contains("11", StringComparison.OrdinalIgnoreCase) || _osName.Contains("26", StringComparison.OrdinalIgnoreCase)) return "Windows 11";
            if (_osName.Contains("10", StringComparison.OrdinalIgnoreCase)) return "Windows 10";
            return string.IsNullOrWhiteSpace(_osName) ? "Windows" : _osName;
        }
    }

    [JsonIgnore]
    public double CpuPercent
    {
        get => _cpuPercent;
        set { _cpuPercent = Math.Clamp(value, 0, 100); OnPropertyChanged(); OnPropertyChanged(nameof(CpuLabel)); }
    }

    [JsonIgnore]
    public double RamPercent
    {
        get => _ramPercent;
        set { _ramPercent = Math.Clamp(value, 0, 100); OnPropertyChanged(); OnPropertyChanged(nameof(RamLabel)); }
    }

    [JsonIgnore]
    public int LatencyMs
    {
        get => _latencyMs;
        set { _latencyMs = Math.Max(0, value); OnPropertyChanged(); OnPropertyChanged(nameof(LatencyLabel)); }
    }

    private DateTime? _lastSeenUtc;
    private long _uptimeSeconds;

    /// <summary>Last time the endpoint answered a probe or streamed telemetry (persisted).</summary>
    public DateTime? LastSeenUtc
    {
        get => _lastSeenUtc;
        set { _lastSeenUtc = value; OnPropertyChanged(); OnPropertyChanged(nameof(HealthLine)); }
    }

    /// <summary>Uptime reported by the agent's telemetry stream (0 = unknown).</summary>
    [JsonIgnore]
    public long UptimeSeconds
    {
        get => _uptimeSeconds;
        set
        {
            if (_uptimeSeconds / 60 == value / 60) { _uptimeSeconds = value; return; }
            _uptimeSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HealthLine));
        }
    }

    /// <summary>"up 3d 4h · seen 14:02" style line for the endpoint cards.</summary>
    [JsonIgnore]
    public string HealthLine
    {
        get
        {
            var parts = new List<string>();
            if (_uptimeSeconds > 0 && (IsOnline || IsConnected))
            {
                var ts = TimeSpan.FromSeconds(_uptimeSeconds);
                parts.Add(ts.TotalDays >= 1 ? $"up {(int)ts.TotalDays}d {ts.Hours}h" : $"up {ts.Hours}h {ts.Minutes}m");
            }
            if (_lastSeenUtc is { } seen)
            {
                var local = seen.ToLocalTime();
                parts.Add(local.Date == DateTime.Today ? $"seen {local:HH:mm}" : $"seen {local:yyyy-MM-dd HH:mm}");
            }
            else
            {
                parts.Add("never seen");
            }
            return string.Join("  ·  ", parts);
        }
    }

    [JsonIgnore]
    public string CpuLabel =>IsOnline || IsConnected ? $"{_cpuPercent:0}%" : "--";

    [JsonIgnore]
    public string RamLabel => IsOnline || IsConnected ? $"{_ramPercent:0}%" : "--";

    [JsonIgnore]
    public string LatencyLabel => IsOnline || IsConnected ? $"{_latencyMs} ms" : "offline";

    [JsonIgnore]
    public string ConnectActionText => IsConnected ? "Disconnect" : "Connect";

    [JsonIgnore]
    public Brush DotBrush => Palette.Fg(StatusTone); // connected = info, online = success, offline = neutral

    [JsonIgnore]
    public Color DotGlowColor => IsConnected || IsOnline
        ? (Palette.Fg(StatusTone) as SolidColorBrush)?.Color ?? Colors.Transparent
        : Colors.Transparent;

    [JsonIgnore]
    public Brush BadgeBackground => Palette.Soft(StatusTone);

    [JsonIgnore]
    private PaletteTone StatusTone => IsConnected ? PaletteTone.Info : IsOnline ? PaletteTone.Success : PaletteTone.Neutral;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public sealed class FleetActivityEvent
{
    public required string Timestamp { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public required Brush BadgeBackground { get; init; }
    public required Brush BadgeForeground { get; init; }
}
