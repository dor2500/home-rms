using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views.Deploy;

/// <summary>
/// The purple bottom bar shown on every Deploy view while apps are selected: stacked icons,
/// "Selected N app(s) so far / Click to view &amp; edit", clear, Install and Add to Pack.
/// </summary>
public partial class SelectionBar : UserControl
{
    private const int MaxIcons = 5;
    private ObservableCollection<WingetAppItem>? _selection;
    private readonly ObservableCollection<WingetAppItem> _icons = [];

    public SelectionBar()
    {
        InitializeComponent();
        IconStack.ItemsSource = _icons;
        ToggleListBtn.Click += (_, _) => { PackPanel.Visibility = Visibility.Collapsed; ListPanel.Visibility = ListPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
        CloseListBtn.Click += (_, _) => ListPanel.Visibility = Visibility.Collapsed;
        ClosePackBtn.Click += (_, _) => PackPanel.Visibility = Visibility.Collapsed;
        AddToPackBtn.Click += (_, _) =>
        {
            ListPanel.Visibility = Visibility.Collapsed;
            PackResultText.Text = "";
            PackPanel.Visibility = PackPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (PackPanel.Visibility == Visibility.Visible) NewPackNameBox.Focus();
        };
    }

    public void Bind(ObservableCollection<WingetAppItem> selection)
    {
        if (_selection is not null) _selection.CollectionChanged -= OnChanged;
        _selection = selection;
        SelectedList.ItemsSource = selection;
        selection.CollectionChanged += OnChanged;
        Refresh();
    }

    public void SetCustomPacks(IEnumerable<WingetPack> packs)
    {
        var list = packs.ToList();
        ExistingPacksList.ItemsSource = list;
        ExistingPacksLabel.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void CloseFlyouts()
    {
        ListPanel.Visibility = Visibility.Collapsed;
        PackPanel.Visibility = Visibility.Collapsed;
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var n = _selection?.Count ?? 0;
        _icons.Clear();
        foreach (var item in (_selection ?? []).Take(MaxIcons)) _icons.Add(item);
        OverflowBadge.Visibility = n > MaxIcons ? Visibility.Visible : Visibility.Collapsed;
        OverflowText.Text = $"+{n - MaxIcons}";
        CountText.Text = $"Selected {n} app{(n == 1 ? "" : "s")} so far";
        ListTitle.Text = $"Selected apps ({n})";
        Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (n == 0) CloseFlyouts();
    }
}
