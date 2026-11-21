using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views.Deploy;

/// <summary>Routed commands raised by cards and buttons anywhere in the Deploy tab (handled by MainWindow.Deploy.cs).</summary>
public static class DeployCommands
{
    /// <summary>Parameter: <see cref="WingetAppItem"/>. Opens the app page.</summary>
    public static readonly RoutedUICommand OpenApp = new("Open app", nameof(OpenApp), typeof(DeployCommands));
    /// <summary>Parameter: <see cref="WingetAppItem"/>. Selects / deselects the app.</summary>
    public static readonly RoutedUICommand ToggleApp = new("Select app", nameof(ToggleApp), typeof(DeployCommands));
    /// <summary>Parameter: category slug. Opens the Apps view filtered by that category.</summary>
    public static readonly RoutedUICommand OpenCategory = new("Open category", nameof(OpenCategory), typeof(DeployCommands));
    /// <summary>Parameter: <see cref="PackCard"/>. Adds the pack's apps to the selection.</summary>
    public static readonly RoutedUICommand SelectPack = new("Select pack", nameof(SelectPack), typeof(DeployCommands));
    /// <summary>Parameter: <see cref="PackCard"/>. Deletes a custom pack.</summary>
    public static readonly RoutedUICommand DeletePack = new("Delete pack", nameof(DeletePack), typeof(DeployCommands));
    /// <summary>Parameter: custom <see cref="WingetPack"/>. Adds the current selection to that pack.</summary>
    public static readonly RoutedUICommand AddSelectionToPack = new("Add to pack", nameof(AddSelectionToPack), typeof(DeployCommands));
}

/// <summary>
/// App icon square: the cached winstall icon, or the monogram while none is loaded. Binds to the
/// DataContext's <c>Icon</c> and <c>Monogram</c> (a <see cref="WingetAppItem"/>).
/// </summary>
public sealed class AppIcon : Border
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AppIcon), new PropertyMetadata(40.0, (d, _) => ((AppIcon)d).ApplySize()));

    private readonly TextBlock _mono;
    private readonly Image _image;

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public AppIcon()
    {
        SetResourceReference(BackgroundProperty, "DeployFieldBrush");
        SetResourceReference(BorderBrushProperty, "DeployBorderBrush");
        BorderThickness = new Thickness(1);
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;

        _mono = new TextBlock
        {
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _mono.SetResourceReference(TextBlock.ForegroundProperty, "DeployAccentTextBrush");
        _mono.SetBinding(TextBlock.TextProperty, new Binding("Monogram") { Mode = BindingMode.OneWay });

        _mono.SetBinding(VisibilityProperty, new Binding("Icon") { Mode = BindingMode.OneWay, Converter = NullToVisibleConverter.Instance });

        _image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _image.SetBinding(Image.SourceProperty, new Binding("Icon") { Mode = BindingMode.OneWay });
        var grid = new Grid();
        grid.Children.Add(_mono);
        grid.Children.Add(_image);
        Child = grid;
        ApplySize();
    }

    private void ApplySize()
    {
        Width = Height = Size;
        CornerRadius = new CornerRadius(Size >= 56 ? 16 : Size >= 36 ? 10 : 7);
        Padding = new Thickness(Size >= 56 ? 10 : Size >= 36 ? 6 : 4);
        _mono.FontSize = Size >= 56 ? 20 : Size >= 36 ? 13 : 10;
    }
}

/// <summary>null -> Visible, anything else -> Collapsed (monogram while no icon is loaded).</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public static readonly NullToVisibleConverter Instance = new();
    public object Convert(object? value, System.Type t, object? p, System.Globalization.CultureInfo c) => value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, System.Type t, object? p, System.Globalization.CultureInfo c) => Binding.DoNothing;
}

/// <summary>Discover "Top Categories" tile.</summary>
public sealed record CategoryTile(string Slug, string Label, Brush Fill, Brush Badge, Brush Ink, Geometry Icon);

/// <summary>A pack card (winstall featured / community pack, Home RMS preset or custom pack).</summary>
public sealed class PackCard
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public required IReadOnlyList<WingetAppItem> Apps { get; init; }
    public int AppCount { get; init; }
    public string Badge { get; init; } = "";
    /// <summary>winstall pack id (null for presets / custom packs).</summary>
    public string? WinstallId { get; init; }
    /// <summary>Package IDs when known in full (presets, custom packs); winstall packs are fetched on select.</summary>
    public IReadOnlyList<string>? PackageIds { get; init; }
    public WingetPack? Pack { get; init; }
    public bool IsCustom => Pack?.IsCustom == true;

    public string MoreText => AppCount > Apps.Count ? $"+{AppCount - Apps.Count} more" : "";
    public Visibility DeleteVisibility => IsCustom ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Essentials section: a category heading and its apps.</summary>
public sealed record EssentialsGroup(string Name, IReadOnlyList<WingetAppItem> Apps);

/// <summary>One cell of the app page "Information" grid.</summary>
public sealed record InfoCell(string Label, string Value, string? Url = null)
{
    public Visibility LinkVisibility => string.IsNullOrEmpty(Url) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TextVisibility => string.IsNullOrEmpty(Url) ? Visibility.Visible : Visibility.Collapsed;
}
