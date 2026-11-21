using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace HomeRms.Controller.Views.Deploy;

public enum DeployView { Discover, Apps, Essentials, Detail, Packs, Install, Files }

/// <summary>
/// winstall-style Deploy tab: top navigation (Discover | Apps | Essentials | Packs | Files), one sub-view at a
/// time, and the purple selection bar pinned to the bottom. Data and deploy logic live in MainWindow.Deploy.cs.
/// </summary>
public partial class DeployShellView : UserControl
{
    private DeployView _current = DeployView.Discover;
    private DeployView _previous = DeployView.Discover;
    private bool _syncingNav;

    public DeployShellView()
    {
        InitializeComponent();
        foreach (var rb in new[] { NavDiscover, NavApps, NavEssentials, NavPacks, NavFiles })
            rb.Checked += OnNavChecked;
        // Narrow windows: drop the brand subtitle and status pill before the nav tabs get squeezed.
        SizeChanged += (_, e) =>
        {
            BrandText.Visibility = e.NewSize.Width < 1060 ? Visibility.Collapsed : Visibility.Visible;
            StatusPill.Visibility = e.NewSize.Width < 960 ? Visibility.Collapsed : Visibility.Visible;
        };
    }

    public DeployView Current => _current;

    /// <summary>The view shown before the current one (the app page's Back button).</summary>
    public DeployView Previous => _previous;

    /// <summary>Raised after a view became visible (MainWindow lazily loads its data).</summary>
    public event EventHandler<DeployView>? ViewShown;

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingNav || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<DeployView>(tag, out var view)) return;
        Show(view);
    }

    public FrameworkElement ViewElement(DeployView v) => v switch
    {
        DeployView.Apps => Apps,
        DeployView.Essentials => Essentials,
        DeployView.Detail => Detail,
        DeployView.Packs => Packs,
        DeployView.Install => Install,
        DeployView.Files => Files,
        _ => Discover,
    };

    public void Show(DeployView view)
    {
        if (view != _current) _previous = _current == DeployView.Detail ? _previous : _current;
        _current = view;
        foreach (DeployView v in Enum.GetValues<DeployView>())
            ViewElement(v).Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;

        _syncingNav = true;
        NavDiscover.IsChecked = view == DeployView.Discover;
        NavApps.IsChecked = view == DeployView.Apps;
        NavEssentials.IsChecked = view == DeployView.Essentials;
        NavPacks.IsChecked = view == DeployView.Packs;
        NavFiles.IsChecked = view == DeployView.Files;
        _syncingNav = false;

        var el = ViewElement(view);
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        ViewShown?.Invoke(this, view);
    }
}
