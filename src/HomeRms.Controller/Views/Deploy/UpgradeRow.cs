using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views.Deploy;

/// <summary>
/// One row of the Install view's Progress panel during Update All: an endpoint summary row
/// (<see cref="IsSummary"/>) or one package on one endpoint. Exposes the same bindings as
/// <see cref="WingetAppItem"/> so the queue template renders both.
/// </summary>
public sealed class UpgradeRow : INotifyPropertyChanged
{
    private DeployItemStatus _status = DeployItemStatus.Queued;
    private int _percent = -1;
    private string _detail = "";
    private string? _stage;

    public required string Endpoint { get; init; }
    /// <summary>Package ID as the agent reports it; empty for the endpoint summary row.</summary>
    public required string Package { get; init; }
    public bool IsSummary => Package.Length == 0;

    public string Name => IsSummary ? $"{Endpoint} · All packages" : $"{Endpoint} · {Package}";
    public string Monogram
    {
        get
        {
            var src = IsSummary ? Endpoint : Package;
            var words = src.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            var mono = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
            return mono.Length == 0 ? "PKG" : mono;
        }
    }
    public ImageSource? Icon => null;

    public DeployItemStatus DeployStatus
    {
        get => _status;
        set { _status = value; Raise(); RaiseState(); }
    }

    /// <summary>0-100, or -1 when unknown (indeterminate bar).</summary>
    public int Percent
    {
        get => _percent;
        set { _percent = value; Raise(); RaiseState(); }
    }

    /// <summary>Busy label override ("Checking", "Refreshing", "Upgrading").</summary>
    public string? Stage
    {
        get => _stage;
        set { _stage = value; Raise(); Raise(nameof(StatusLabel)); }
    }

    public string StatusDetail
    {
        get => _detail;
        set { _detail = value; Raise(); Raise(nameof(DetailVisibility)); }
    }

    public bool IsBusy => _status is DeployItemStatus.Queued or DeployItemStatus.Downloading or DeployItemStatus.Installing;
    public bool IsFinished => !IsBusy && _status != DeployItemStatus.Pending;
    public double ProgressValue => Math.Clamp(_percent, 0, 100);
    public bool IsIndeterminate => IsBusy && _percent < 0;
    public Visibility ProgressVisibility => IsBusy && _status != DeployItemStatus.Queued ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => string.IsNullOrEmpty(_detail) ? Visibility.Collapsed : Visibility.Visible;

    public string StatusLabel
    {
        get
        {
            var pct = _percent >= 0 ? $" {_percent}%" : "";
            return _status switch
            {
                DeployItemStatus.Queued => _stage ?? "Queued",
                DeployItemStatus.Downloading => (_stage ?? "Downloading") + pct,
                DeployItemStatus.Installing => (_stage ?? "Upgrading") + pct,
                DeployItemStatus.Success => IsSummary ? "Completed" : "Upgraded",
                DeployItemStatus.Skipped => IsSummary ? "Up to date" : "Skipped",
                DeployItemStatus.Failed => "Failed",
                DeployItemStatus.Cancelled => "Cancelled",
                _ => "Ready",
            };
        }
    }

    private void RaiseState()
    {
        Raise(nameof(StatusLabel));
        Raise(nameof(ProgressValue));
        Raise(nameof(IsIndeterminate));
        Raise(nameof(ProgressVisibility));
        Raise(nameof(IsBusy));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
