using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HomeRms.Controller.Services;
using HomeRms.Controller.Themes;

namespace HomeRms.Controller;

/// <summary>
/// Shell chrome: collapsible navigation rail, navigation badges, view transitions,
/// persistent Live Console drawer, and sort-preserving DataGrid binding.
/// </summary>
public partial class MainWindow
{
    private const double SidebarExpandedWidth = 192;
    private const double SidebarCollapsedWidth = 52;
    private const double MinConsoleHeight = 120;
    private const double MaxConsoleHeight = 520;

    private UiSettings _ui = new();
    private ICollectionView? _consoleView;
    private string _consoleFilter = "All";
    private bool _consoleChromeHidden;

    private void InitializeShell()
    {
        _ui = UiSettings.Load();

        _sidebarCollapsed = _ui.SidebarCollapsed;
        ApplySidebarState(animate: false);

        _consoleFilter = string.IsNullOrWhiteSpace(_ui.ConsoleFilter) ? "All" : _ui.ConsoleFilter;
        SelectConsoleFilterButton(_consoleFilter);

        _consoleView = new ListCollectionView(LogBus.Entries)
        {
            Filter = FilterConsoleEntry
        };
        ConsoleList.ItemsSource = _consoleView;

        LogBus.Published += OnLogBusPublished;
        Closed += (_, _) => LogBus.Published -= OnLogBusPublished;

        SetConsoleOpen(_ui.ConsoleOpen, persist: false);
        UpdateConsoleCount();
        UpdateNavBadges();
    }

    private void OnLogBusPublished(LogEntry entry)
    {
        if (_consoleView is null) return;
        UpdateConsoleCount();

        if (ConsoleDrawer.Visibility == Visibility.Visible)
        {
            if (ConsoleList.Items.Count > 0)
                ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
        }
        else if (ConsoleUnreadDot is not null)
        {
            ConsoleUnreadDot.Visibility = Visibility.Visible;
        }
    }

    private bool FilterConsoleEntry(object obj)
    {
        if (obj is not LogEntry entry) return false;

        if (!string.Equals(_consoleFilter, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.Source, _consoleFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ConsoleSearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        return entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectConsoleFilterButton(string filter)
    {
        var target = filter switch
        {
            LogBus.Activity => ConsoleFilterActivity,
            LogBus.Deploy => ConsoleFilterDeploy,
            LogBus.Scripts => ConsoleFilterScripts,
            LogBus.Alerts => ConsoleFilterAlerts,
            _ => ConsoleFilterAll
        };
        if (target is not null)
            target.IsChecked = true;
    }

    private void UpdateConsoleCount()
    {
        // Called from Checked handlers while InitializeComponent is still building the tree.
        if (ConsoleCountText is null || ConsoleList is null) return;
        var count = _consoleView is ListCollectionView lcv ? lcv.Count : ConsoleList.Items.Count;
        ConsoleCountText.Text = count == 1 ? "1 line" : $"{count} lines";
    }

    private void ApplySidebarState(bool animate)
    {
        var targetWidth = _sidebarCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth;
        if (animate)
        {
            var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            RailBorder.BeginAnimation(WidthProperty, anim);
        }
        else
        {
            RailBorder.BeginAnimation(WidthProperty, null);
            RailBorder.Width = targetWidth;
        }

        var labelVis = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        var sepVis = _sidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;

        NavLabelAbout.Visibility = labelVis;
        NavLabelOverview.Visibility = labelVis;
        NavLabelFleet.Visibility = labelVis;
        NavLabelMonitor.Visibility = labelVis;
        NavLabelDesktop.Visibility = labelVis;
        NavLabelShell.Visibility = labelVis;
        NavLabelSettings.Visibility = labelVis;
        NavLabelWebUi.Visibility = labelVis;
        NavLabelDeploy.Visibility = labelVis;
        NavLabelUpdates.Visibility = labelVis;
        UpdateFeatureNavLabels();

        NavGroupAdmin.Visibility = labelVis;
        SidebarSectionLabel.Visibility = labelVis;
        NavGroupManage.Visibility = labelVis;
        NavGroupAutomate.Visibility = labelVis;

        NavSepAdmin.Visibility = sepVis;
        NavSepMonitor.Visibility = sepVis;
        NavSepManage.Visibility = sepVis;
        NavSepAutomate.Visibility = sepVis;

        SidebarFooterBorder.Visibility = labelVis;
        UpdateNavBadges();
    }

    private void UpdateNavBadges()
    {
        if (NavBadgeOverview is null || NavBadgeFleet is null) return;

        if (_sidebarCollapsed)
        {
            NavBadgeOverview.Visibility = Visibility.Collapsed;
            NavBadgeFleet.Visibility = Visibility.Collapsed;
            return;
        }

        var unread = _alerts.UnreadCount;
        NavBadgeOverviewText.Text = unread.ToString();
        NavBadgeOverview.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = _machines.Count;
        var online = _machines.Count(m => m.IsOnline || m.IsConnected);
        NavBadgeFleetText.Text = $"{online}/{total}";
        NavBadgeFleet.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AnimateActivePanel()
    {
        if (WorkspaceGrid is null) return;

        foreach (UIElement child in WorkspaceGrid.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;

            if (child.RenderTransform is not TranslateTransform translate)
            {
                translate = new TranslateTransform();
                child.RenderTransform = translate;
            }

            var fade = new DoubleAnimation(0.4, 1.0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slide = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            child.BeginAnimation(OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private void SetConsoleOpen(bool open, bool persist)
    {
        if (persist)
        {
            _ui.ConsoleOpen = open;
            _ui.Save();
        }

        if (_consoleChromeHidden)
        {
            ConsoleRow.Height = new GridLength(0);
            ConsoleSplitter.Visibility = Visibility.Collapsed;
            ConsoleDrawer.Visibility = Visibility.Collapsed;
            return;
        }

        if (open)
        {
            var height = Math.Clamp(_ui.ConsoleHeight, MinConsoleHeight, MaxConsoleHeight);
            ConsoleRow.Height = new GridLength(height);
            ConsoleSplitter.Visibility = Visibility.Visible;
            ConsoleDrawer.Visibility = Visibility.Visible;
            if (ConsoleUnreadDot is not null)
                ConsoleUnreadDot.Visibility = Visibility.Collapsed;
            UpdateConsoleCount();
            if (ConsoleList.Items.Count > 0)
                ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
        }
        else
        {
            ConsoleRow.Height = new GridLength(0);
            ConsoleSplitter.Visibility = Visibility.Collapsed;
            ConsoleDrawer.Visibility = Visibility.Collapsed;
        }
    }

    private void SetConsoleChromeHidden(bool hidden)
    {
        _consoleChromeHidden = hidden;
        SetConsoleOpen(_ui.ConsoleOpen, persist: false);
    }

    private int BindPreservingSort<T>(DataGrid grid, IEnumerable<T> items, Predicate<object>? filter = null)
    {
        var savedSorts = grid.Items.SortDescriptions.ToList();
        var savedDirections = grid.Columns.Select(c => c.SortDirection).ToList();

        var view = new ListCollectionView(items.ToList())
        {
            Filter = filter
        };
        foreach (var sort in savedSorts)
            view.SortDescriptions.Add(sort);

        grid.ItemsSource = view;

        for (var i = 0; i < grid.Columns.Count && i < savedDirections.Count; i++)
            grid.Columns[i].SortDirection = savedDirections[i];

        return view.Count;
    }

    private void MirrorDeployLineToConsole(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var tone = PaletteTone.Neutral;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            tone = PaletteTone.Danger;
        }
        else if (line.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("completed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("installed", StringComparison.OrdinalIgnoreCase))
        {
            tone = PaletteTone.Success;
        }

        LogBus.Publish(LogBus.Deploy, "deploy", line, tone);
    }

    private void OnToggleConsoleClick(object sender, RoutedEventArgs e)
        => SetConsoleOpen(ConsoleDrawer.Visibility != Visibility.Visible, persist: true);

    private void OnConsoleSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var height = ConsoleRow.ActualHeight;
        if (height <= 0) return;

        _ui.ConsoleHeight = Math.Clamp(height, MinConsoleHeight, MaxConsoleHeight);
        ConsoleRow.Height = new GridLength(_ui.ConsoleHeight);
        _ui.Save();
    }

    private void OnConsoleSearchChanged(object sender, TextChangedEventArgs e)
    {
        _consoleView?.Refresh();
        UpdateConsoleCount();
    }

    private void OnConsoleCopyClick(object sender, RoutedEventArgs e)
    {
        if (_consoleView is null) return;
        var sb = new StringBuilder();
        foreach (var item in _consoleView)
        {
            if (item is LogEntry entry)
                sb.AppendLine(entry.ToString());
        }
        if (sb.Length > 0)
            Clipboard.SetText(sb.ToString());
    }

    private void OnConsoleClearClick(object sender, RoutedEventArgs e)
    {
        LogBus.Clear();
        _consoleView?.Refresh();
        UpdateConsoleCount();
    }

    private void OnConsoleFilterChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        _consoleFilter = tag;
        if (_consoleView is null) return; // initial IsChecked during InitializeComponent
        _consoleView?.Refresh();
        UpdateConsoleCount();

        if (_ui.ConsoleFilter != tag)
        {
            _ui.ConsoleFilter = tag;
            _ui.Save();
        }
    }
}
