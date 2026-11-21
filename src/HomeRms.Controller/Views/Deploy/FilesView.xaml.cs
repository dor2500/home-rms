using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace HomeRms.Controller.Views.Deploy;

/// <summary>Files: drag-and-drop / browse local installers and scripts, run them on the connected agent.</summary>
public partial class FilesView : UserControl
{
    public FilesView()
    {
        InitializeComponent();
        FileList.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is Button { Name: "RemoveBtn", Tag: { } item })
            {
                RemoveRequested?.Invoke(this, item);
                e.Handled = true;
            }
        }));
        ((INotifyCollectionChanged)FileList.Items).CollectionChanged += (_, _) =>
            EmptyText.Visibility = FileList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Raised with the row's <c>DeployFileItem</c> when its remove button is clicked.</summary>
    public event EventHandler<object>? RemoveRequested;
}
