using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace HomeRms.Controller.Models;

/// <summary>A saved agent endpoint the controller can monitor and connect to.</summary>
public sealed class MachineProfile : INotifyPropertyChanged
{
    private string _name = "";
    private string _host = "";
    private int _port = 7443;
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
    public string OsGlyph => _osName.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "🐧" : "⊞";

    [JsonIgnore]
    public string OsTag
    {
        get
        {
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
