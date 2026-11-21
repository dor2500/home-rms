using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views.Deploy;

/// <summary>
/// App page: icon, name, "by Publisher", Install / Upgrade command with a version dropdown and Copy,
/// description, tag chips and the Information grid. DataContext is the <see cref="WingetAppItem"/>.
/// </summary>
public partial class AppDetailView : UserControl
{
    public static readonly RoutedUICommand OpenLink = new("Open link", nameof(OpenLink), typeof(AppDetailView));
    private const int VisibleTags = 6;

    private List<string> _tags = [];
    private bool _showAllTags;

    public AppDetailView()
    {
        InitializeComponent();
        TabInstall.Checked += (_, _) => UpdateCommand();
        TabUpgrade.Checked += (_, _) => UpdateCommand();
        VersionCombo.SelectionChanged += (_, _) =>
        {
            // The chosen version is also the pin used by the Install view and remote deploys.
            if (Item is { } i && VersionCombo.SelectedItem is VersionOption v && i.VersionOptions.Contains(v) && !Equals(i.SelectedVersionOption, v))
                i.SelectedVersionOption = v;
            UpdateCommand();
        };
        CommandBindings.Add(new CommandBinding(OpenLink, (_, e) =>
        {
            if (e.Parameter is string url && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
        }));
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= OnItemChanged;
            if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += OnItemChanged;
            UpdateSelectButton();
        };
    }

    public WingetAppItem? Item => DataContext as WingetAppItem;

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WingetAppItem.IsSelected)) UpdateSelectButton();
        if (e.PropertyName is nameof(WingetAppItem.Name)) InstallLabel.Text = $"Install or upgrade {Item?.Name} with winget:";
    }

    /// <summary>Shows <paramref name="item"/> with what is known now (details arrive via <see cref="SetDetails"/>).</summary>
    public void ShowItem(WingetAppItem item)
    {
        DataContext = item;
        item.EnsureVersionOptions();
        InstallLabel.Text = $"Install or upgrade {item.Name} with winget:";
        TabInstall.IsChecked = true;
        VersionCombo.ItemsSource = item.VersionOptions;
        VersionCombo.SelectedItem = item.SelectedVersionOption ?? item.VersionOptions.FirstOrDefault();
        DescriptionText.Text = string.IsNullOrWhiteSpace(item.Description) ? "Loading details..." : item.Description;
        SetTags([]);
        InfoList.ItemsSource = BasicInfo(item);
        StatusText.Text = "";
        CopyText.Text = "Copy";
        UpdateCommand();
    }

    public void SetDetails(WingetAppItem item, WinstallAppDetails? d, string? note)
    {
        if (!ReferenceEquals(Item, item)) return;
        if (d is not null)
        {
            var desc = d.FullDescription.Length > d.Description.Length ? d.FullDescription : d.Description;
            DescriptionText.Text = desc.Length > 0 ? desc : item.Description;
            SetTags(d.Tags.ToList());
            var cells = new List<InfoCell>
            {
                new("Publisher", d.Publisher),
                new("Version", d.LatestVersion.Length > 0 ? d.LatestVersion : item.Version),
                new("Updated", d.UpdatedUtc is { } u ? u.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "Unknown"),
                new("License", d.License.Length > 0 ? d.License : "Unknown", d.LicenseUrl.Length > 0 ? d.LicenseUrl : null),
                new("Package ID", d.Id),
                new("Copyright", d.Copyright.Length > 0 ? d.Copyright : "Not specified"),
            };
            if (d.Homepage.Length > 0) cells.Add(new InfoCell("Homepage", d.Homepage.Replace("https://", "").Replace("http://", "").TrimEnd('/'), d.Homepage));
            if (d.Moniker.Length > 0) cells.Add(new InfoCell("Moniker", d.Moniker));
            InfoList.ItemsSource = cells;
        }
        else if (string.IsNullOrWhiteSpace(item.Description) || DescriptionText.Text == "Loading details...")
        {
            DescriptionText.Text = string.IsNullOrWhiteSpace(item.Description) ? "No description available." : item.Description;
        }
        VersionCombo.ItemsSource = item.VersionOptions;
        VersionCombo.SelectedItem = item.SelectedVersionOption ?? item.VersionOptions.FirstOrDefault();
        StatusText.Text = note ?? "";
        UpdateCommand();
    }

    private static List<InfoCell> BasicInfo(WingetAppItem item) =>
    [
        new("Publisher", item.Publisher),
        new("Version", string.IsNullOrWhiteSpace(item.Version) ? "Unknown" : item.Version),
        new("Package ID", item.Id),
    ];

    private void SetTags(List<string> tags)
    {
        _tags = tags;
        _showAllTags = false;
        RenderTags();
    }

    private void RenderTags()
    {
        TagsPanel.Children.Clear();
        var shown = _showAllTags ? _tags : _tags.Take(VisibleTags).ToList();
        foreach (var t in shown)
            TagsPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("DeployChip"),
                Child = new TextBlock { Text = t, FontSize = 12.5, Foreground = (Brush)FindResource("TextPrimaryBrush") },
            });
        if (!_showAllTags && _tags.Count > VisibleTags)
        {
            var more = new Button
            {
                Style = (Style)FindResource("DeployLinkButton"),
                Content = $"+{_tags.Count - VisibleTags} more",
                Margin = new Thickness(0, 0, 8, 8),
            };
            more.Click += (_, _) => { _showAllTags = true; RenderTags(); };
            TagsPanel.Children.Add(more);
        }
    }

    private void UpdateCommand()
    {
        if (Item is not { } item) return;
        var opt = VersionCombo.SelectedItem as VersionOption;
        var version = opt is { IsLatest: false } ? opt.Value : null;
        try { CommandBox.Text = WingetScriptGenerator.SingleCommand(item.Id, version, TabUpgrade.IsChecked == true); }
        catch (ArgumentException ex) { CommandBox.Text = ex.Message; }
        CopyText.Text = "Copy";
    }

    private void UpdateSelectButton()
    {
        var selected = Item?.IsSelected == true;
        SelectText.Text = selected ? "Selected" : "Select app";
        SelectIcon.Data = (Geometry)FindResource(selected ? "DGeoCheck" : "DGeoPlus");
        SelectBtn.Background = (Brush)FindResource(selected ? "DeployAccentSoftBrush" : "DeployCardBrush");
        SelectBtn.BorderBrush = (Brush)FindResource(selected ? "DeployAccentLineBrush" : "DeployBorderBrush");
    }
}
