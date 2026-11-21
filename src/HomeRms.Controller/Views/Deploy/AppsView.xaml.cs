using System;
using System.Windows;
using System.Windows.Controls;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views.Deploy;

/// <summary>
/// Apps: "All apps (N)" heading, search, category filter pills and a 4/3/2-column card grid that loads
/// 60 more cards whenever the list scrolls near its end (MainWindow.Deploy.cs supplies the pages).
/// </summary>
public partial class AppsView : DeployGridView
{
    private bool _syncing;

    public AppsView()
    {
        InitializeComponent();
        AddPill("", "All");
        foreach (var c in DeployCategory.All) AddPill(c.Slug, c.Label);
        Scroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange > 0 && Scroll.ScrollableHeight > 0 && Scroll.VerticalOffset >= Scroll.ScrollableHeight - 500)
                NearEnd?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Selected category slug ("" = all).</summary>
    public string CategorySlug { get; private set; } = "";

    public event EventHandler? CategoryChanged;
    public event EventHandler? NearEnd;

    public void SetCategory(string slug)
    {
        _syncing = true;
        foreach (var child in CategoryPills.Children)
            if (child is RadioButton rb) rb.IsChecked = (string)rb.Tag == slug;
        _syncing = false;
        CategorySlug = slug;
    }

    private void AddPill(string slug, string label)
    {
        var rb = new RadioButton
        {
            Content = label,
            Tag = slug,
            GroupName = "AppsCategory",
            IsChecked = slug.Length == 0,
            Style = (Style)FindResource("DeployPillTab"),
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 12.5,
        };
        rb.Checked += (_, _) =>
        {
            if (_syncing) return;
            CategorySlug = slug;
            CategoryChanged?.Invoke(this, EventArgs.Empty);
        };
        CategoryPills.Children.Add(rb);
    }
}
