using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views;

/// <summary>One side of the dual-pane file manager (local or remote, via <see cref="IFileSource"/>).</summary>
public partial class FilePaneView : UserControl
{
    /// <summary>Clipboard/drag format carrying full remote paths (string[]).</summary>
    public const string RemotePathsFormat = "HomeRms.RemotePaths";
    /// <summary>Drag format identifying the pane a drag started in (so drops on the same pane are ignored).</summary>
    public const string SourcePaneFormat = "HomeRms.SourcePane";

    private readonly ObservableCollection<FileRow> _rows = new();
    private readonly ICollectionView _view;
    private string _sortProperty = nameof(FileRow.Name);
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private Point _dragStart;
    private bool _suppressDriveChange;
    private CancellationTokenSource? _listCts;

    public FilePaneView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_rows);
        ApplySort();
        FilesList.ItemsSource = _view;
    }

    public IFileSource? Source { get; private set; }
    public string CurrentPath { get; private set; } = "";
    public string ParentPath { get; private set; } = "";
    public string PaneId { get; } = Guid.NewGuid().ToString("N");
    public IReadOnlyCollection<FileRow> Rows => _rows;

    public IReadOnlyList<FileRow> SelectedRows => FilesList.SelectedItems.Cast<FileRow>().ToList();

    /// <summary>Raised when something is dropped on this pane (the host decides upload / download).</summary>
    public event Action<FilePaneView, IDataObject>? DropReceived;

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Attaches a file source and opens <paramref name="initialPath"/> (or its first drive).</summary>
    public async Task AttachAsync(IFileSource? source, string? initialPath = null)
    {
        Source = source;
        _rows.Clear();
        CurrentPath = ParentPath = "";
        PathBox.Text = "";
        _suppressDriveChange = true;
        DriveCombo.ItemsSource = null;
        _suppressDriveChange = false;
        ShowError(null);
        CountText.Text = "";
        if (source is null) return;

        try
        {
            var drives = await source.GetDrivesAsync(CancellationToken.None);
            _suppressDriveChange = true;
            DriveCombo.ItemsSource = drives;
            _suppressDriveChange = false;
            var start = initialPath ?? drives.FirstOrDefault()?.Name;
            if (start is not null) await NavigateAsync(start);
        }
        catch (Exception ex)
        {
            ShowError(AgentErrors.Describe(ex));
        }
    }

    public Task RefreshAsync() => CurrentPath.Length > 0 ? NavigateAsync(CurrentPath) : Task.CompletedTask;

    public async Task NavigateAsync(string path)
    {
        if (Source is null || string.IsNullOrWhiteSpace(path)) return;
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        var keep = SelectedRows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            Cursor = Cursors.AppStarting;
            var listing = await Source.ListAsync(path.Trim(), cts.Token);
            if (cts.IsCancellationRequested) return;

            var samePath = string.Equals(listing.Path, CurrentPath, StringComparison.OrdinalIgnoreCase);
            CurrentPath = listing.Path;
            ParentPath = listing.Parent;
            PathBox.Text = listing.Path;
            _rows.Clear();
            foreach (var r in listing.Entries) _rows.Add(r);
            CountText.Text = $"{listing.Entries.Count(e => e.IsDir)} folders · {listing.Entries.Count(e => !e.IsDir)} files" +
                             (listing.Truncated ? " · TRUNCATED" : "");
            if (samePath)
                foreach (var r in _rows.Where(r => keep.Contains(r.Name))) FilesList.SelectedItems.Add(r);
            SyncDriveCombo();
            ShowError(listing.Truncated ? "Only the first 10,000 entries are shown." : null);
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            ShowError(AgentErrors.Describe(ex));
            PathBox.Text = CurrentPath;
        }
        catch
        {
            // Superseded by a newer navigation.
        }
        finally
        {
            Cursor = null;
        }
    }

    private void SyncDriveCombo()
    {
        if (DriveCombo.ItemsSource is not IEnumerable<DriveItem> drives) return;
        var match = drives
            .Where(d => CurrentPath.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Name.Length)
            .FirstOrDefault();
        _suppressDriveChange = true;
        DriveCombo.SelectedItem = match;
        _suppressDriveChange = false;
    }

    // ---------------- Sorting (ICollectionView keeps it across refreshes) ----------------
    private void ApplySort()
    {
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(nameof(FileRow.DirFirst), ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new SortDescription(_sortProperty, _sortDirection));
            if (_sortProperty != nameof(FileRow.Name))
                _view.SortDescriptions.Add(new SortDescription(nameof(FileRow.Name), ListSortDirection.Ascending));
        }
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string prop } header) return;
        _sortDirection = _sortProperty == prop && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortProperty = prop;
        ApplySort();

        if (FilesList.View is GridView gv)
            foreach (var col in gv.Columns)
                if (col.Header is GridViewColumnHeader h && h.Tag is string)
                    h.Content = ((string)h.Content).TrimEnd(' ', '▲', '▼') + (h == header ? (_sortDirection == ListSortDirection.Ascending ? " ▲" : " ▼") : "");
    }

    // ---------------- Navigation ----------------
    private async void OnDriveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDriveChange || DriveCombo.SelectedItem is not DriveItem d) return;
        await NavigateAsync(d.Name);
    }

    private async void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (ParentPath.Length > 0) await NavigateAsync(ParentPath);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await NavigateAsync(PathBox.Text);
    }

    private async void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromPoint(e.OriginalSource) is { IsDir: true } row) await NavigateAsync(row.FullPath);
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when FilesList.SelectedItem is FileRow { IsDir: true } row:
                e.Handled = true;
                await NavigateAsync(row.FullPath);
                break;
            case Key.Back:
                e.Handled = true;
                if (ParentPath.Length > 0) await NavigateAsync(ParentPath);
                break;
            case Key.F5:
                e.Handled = true;
                await RefreshAsync();
                break;
            case Key.F2:
                e.Handled = true;
                OnRenameClick(sender, e);
                break;
            case Key.Delete:
                e.Handled = true;
                OnDeleteClick(sender, e);
                break;
        }
    }

    private static FileRow? ItemFromPoint(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not ListViewItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListViewItem)?.DataContext as FileRow;
    }

    // ---------------- Operations ----------------
    private static string? ValidateName(string name)
    {
        if (name.Length == 0) return "Enter a name.";
        if (name is "." or "..") return "Invalid name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            return "The name contains invalid characters.";
        return null;
    }

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (Source is null || CurrentPath.Length == 0) return;
        var name = InputDialog.Prompt(this, "New folder", $"New folder in {CurrentPath}:", "New folder", ValidateName);
        if (name is null) return;
        try
        {
            await Source.CreateDirectoryAsync(Source.Combine(CurrentPath, name), CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError("Create folder failed: " + AgentErrors.Describe(ex)); }
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (Source is null) return;
        if (SelectedRows is not [var row])
        {
            ShowError("Select exactly one item to rename.");
            return;
        }
        var name = InputDialog.Prompt(this, "Rename", $"New name for '{row.Name}':", row.Name, ValidateName);
        if (name is null || name == row.Name) return;
        try
        {
            await Source.RenameAsync(row.FullPath, Source.Combine(CurrentPath, name), CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError("Rename failed: " + AgentErrors.Describe(ex)); }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Source is null) return;
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        var dirs = rows.Count(r => r.IsDir);
        var list = string.Join("\n", rows.Take(10).Select(r => "  • " + r.Name + (r.IsDir ? "\\" : "")));
        if (rows.Count > 10) list += $"\n  … and {rows.Count - 10} more";
        var where = Source.IsRemote ? $"on {Source.DisplayName}" : "on this PC";
        var msg = $"Permanently delete {rows.Count} item(s) {where}?\n\n{list}" +
                  (dirs > 0 ? $"\n\n{dirs} folder(s) will be deleted with ALL of their contents." : "") +
                  "\n\nThis does not use the Recycle Bin and cannot be undone.";
        if (MessageBox.Show(Window.GetWindow(this)!, msg, "Home RMS — Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var errors = new List<string>();
        foreach (var r in rows)
        {
            try { await Source.DeleteAsync(r.FullPath, recursive: r.IsDir, CancellationToken.None); }
            catch (Exception ex) { errors.Add($"{r.Name}: {AgentErrors.Describe(ex)}"); }
        }
        await RefreshAsync();
        if (errors.Count > 0) ShowError("Delete failed — " + string.Join("; ", errors.Take(3)) + (errors.Count > 3 ? " …" : ""));
    }

    // ---------------- Drag & drop ----------------
    private void OnListMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Source is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (ItemFromPoint(e.OriginalSource) is null) return;
        var rows = SelectedRows;
        if (rows.Count == 0) return;

        var data = new DataObject();
        data.SetData(SourcePaneFormat, PaneId);
        var paths = rows.Select(r => r.FullPath).ToArray();
        if (Source.IsRemote) data.SetData(RemotePathsFormat, paths);
        else data.SetData(DataFormats.FileDrop, paths); // also lets local items be dropped into Explorer
        DragDrop.DoDragDrop(FilesList, data, DragDropEffects.Copy);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        var fromSelf = e.Data.GetDataPresent(SourcePaneFormat) && (e.Data.GetData(SourcePaneFormat) as string) == PaneId;
        var acceptable = Source is not null && !fromSelf &&
                         (Source.IsRemote ? e.Data.GetDataPresent(DataFormats.FileDrop) : e.Data.GetDataPresent(RemotePathsFormat));
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        var fromSelf = e.Data.GetDataPresent(SourcePaneFormat) && (e.Data.GetData(SourcePaneFormat) as string) == PaneId;
        if (fromSelf || Source is null) return;
        e.Handled = true;
        DropReceived?.Invoke(this, e.Data);
    }
}
