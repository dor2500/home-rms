using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

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
        set { _host = value; OnPropertyChanged(); OnPropertyChanged(nameof(Address)); OnPropertyChanged(nameof(Subtitle)); }
    }

    public int Port
    {
        get => _port;
        set { _port = value; OnPropertyChanged(); OnPropertyChanged(nameof(Address)); OnPropertyChanged(nameof(Subtitle)); }
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

    [JsonIgnore]
    public string ModeBadge => _kind switch
    {
        EndpointKind.WindowsAgent => "Agent: mTLS",
        EndpointKind.WindowsAgentless => "Agentless: WinRM / RDP",
        EndpointKind.HomeAssistant => "Home Assistant",
        EndpointKind.Nas => "NAS / Storage",
        EndpointKind.Linux => "Linux / SSH",
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
        set { _osName = value; OnPropertyChanged(); OnPropertyChanged(nameof(OsTag)); OnPropertyChanged(nameof(OsGlyph)); }
    }

    [JsonIgnore]
    public string OsGlyph => _kind switch
    {
        EndpointKind.HomeAssistant => "HA",
        EndpointKind.Nas => "NAS",
        EndpointKind.Linux => "LNX",
        EndpointKind.WebService => "WEB",
        EndpointKind.WindowsAgentless => "RDP",
        _ => _osName.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "LNX" : "WIN",
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
            if (_osName.Contains("Linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
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

    [JsonIgnore]
    public string CpuLabel => IsOnline || IsConnected ? $"{_cpuPercent:0}%" : "--";

    [JsonIgnore]
    public string RamLabel => IsOnline || IsConnected ? $"{_ramPercent:0}%" : "--";

    [JsonIgnore]
    public string LatencyLabel => IsOnline || IsConnected ? $"{_latencyMs} ms" : "offline";

    [JsonIgnore]
    public string ConnectActionText => IsConnected ? "Disconnect" : "Connect";

    [JsonIgnore]
    public Brush DotBrush => IsConnected
        ? new SolidColorBrush(Color.FromRgb(14, 165, 233))   // #0EA5E9 Connected Cyan
        : IsOnline
            ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) // #10B981 Glowing Emerald
            : new SolidColorBrush(Color.FromRgb(100, 116, 139)); // Muted Slate

    [JsonIgnore]
    public Color DotGlowColor => IsConnected
        ? Color.FromRgb(14, 165, 233)
        : IsOnline
            ? Color.FromRgb(16, 185, 129)
            : Color.FromArgb(0, 0, 0, 0);

    [JsonIgnore]
    public Brush BadgeBackground => IsConnected
        ? new SolidColorBrush(Color.FromArgb(45, 14, 165, 233))
        : IsOnline
            ? new SolidColorBrush(Color.FromArgb(40, 16, 185, 129))
            : new SolidColorBrush(Color.FromArgb(30, 100, 116, 139));

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
