using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HomeRms.Controller.Views;

/// <summary>Small themed single-line input prompt (rename, new folder, MAC address ...).</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _error;
    private readonly Func<string, string?>? _validate;

    private InputDialog(string title, string label, string initial, Func<string, string?>? validate)
    {
        _validate = validate;
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        root.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        _box = new TextBox { Text = initial, Padding = new Thickness(6, 4, 6, 4) };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Accept();
            else if (e.Key == Key.Escape) DialogResult = false;
        };
        root.Children.Add(_box);

        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "GhostButton");
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            _box.Focus();
            var dot = initial.LastIndexOf('.');
            if (dot > 0) _box.Select(0, dot);
            else _box.SelectAll();
        };
    }

    private void Accept()
    {
        var err = _validate?.Invoke(_box.Text.Trim());
        if (err is not null)
        {
            _error.Text = err;
            _error.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    /// <summary>Shows the prompt; returns the trimmed text or null when cancelled.</summary>
    public static string? Prompt(DependencyObject? ownerHint, string title, string label, string initial = "", Func<string, string?>? validate = null)
    {
        var dlg = new InputDialog(title, label, initial, validate)
        {
            Owner = ownerHint is null ? Application.Current?.MainWindow : Window.GetWindow(ownerHint)
        };
        return dlg.ShowDialog() == true ? dlg._box.Text.Trim() : null;
    }
}
