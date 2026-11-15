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
        set { _isOnline = value; OnPropertyChanged(); OnPropertyChanged(nameof(DotBrush)); }
    }

    [JsonIgnore]
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(DotBrush)); OnPropertyChanged(nameof(BadgeBackground)); }
    }

    [JsonIgnore]
    public string StatusLabel
    {
        get => _statusLabel;
        set { _statusLabel = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public Brush DotBrush => IsConnected
        ? new SolidColorBrush(Color.FromRgb(59, 130, 246))   // Accent Blue
        : IsOnline
            ? new SolidColorBrush(Color.FromRgb(34, 197, 94)) // Online Green
            : new SolidColorBrush(Color.FromRgb(107, 114, 128)); // Muted Grey

    [JsonIgnore]
    public Brush BadgeBackground => IsConnected
        ? new SolidColorBrush(Color.FromArgb(45, 59, 130, 246))
        : IsOnline
            ? new SolidColorBrush(Color.FromArgb(40, 34, 197, 94))
            : new SolidColorBrush(Color.FromArgb(35, 107, 114, 128));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
