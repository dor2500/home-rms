using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Google.Protobuf;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Views;

/// <summary>A queued / running / finished file transfer.</summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    private long _done;
    private long _total;
    private string _status = "Queued";
    private bool _isActive = true;
    private bool _isError;

    public required bool IsUpload { get; init; }
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public CancellationTokenSource Cts { get; } = new();

    public string DirectionText => IsUpload ? "UP" : "DOWN";
    public string Route => $"{SourcePath}  →  {DestinationPath}";
    public long Done { get => _done; set { _done = value; Raise(); Raise(nameof(Percent)); Raise(nameof(SizeText)); } }
    public long Total { get => _total; set { _total = value; Raise(); Raise(nameof(Percent)); Raise(nameof(SizeText)); } }
    public double Percent => _total > 0 ? Math.Min(100, _done * 100.0 / _total) : (_isActive ? 0 : 100);
    public string SizeText => _total > 0 ? $"{FileRow.FormatSize(_done)} / {FileRow.FormatSize(_total)}" : FileRow.FormatSize(_done);
    public string Status { get => _status; set { _status = value; Raise(); } }
    public bool IsActive { get => _isActive; set { _isActive = value; Raise(); Raise(nameof(Percent)); } }
    public bool IsError { get => _isError; set { _isError = value; Raise(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>Dual-pane remote file manager (local disk on the left, paired agent on the right).</summary>
public partial class FileManagerView : UserControl
{
    private const int ChunkSize = 256 * 1024;

    private readonly ObservableCollection<TransferItem> _transfers = new();
    private readonly SemaphoreSlim _transferSlots = new(2, 2);
    private FeatureContext? _ctx;
    private RemoteFileSource? _remote;
    private bool _localAttached;

    public FileManagerView()
    {
        InitializeComponent();
        TransfersList.ItemsSource = _transfers;
        LocalPane.Title = "This PC (local)";
        RemotePane.Title = "Remote — choose an endpoint and click Open";
        LocalPane.DropReceived += OnPaneDrop;
        RemotePane.DropReceived += OnPaneDrop;
    }

    public void Initialize(FeatureContext ctx)
    {
        _ctx = ctx;
        ActionBar.Context = ctx;
        ActionBar.TargetsProvider = () => EndpointCombo.SelectedItem is MachineProfile m ? [m] : [];
        RefreshEndpointList();
    }

    /// <summary>Called whenever the view becomes visible.</summary>
    public async void OnShown()
    {
        RefreshEndpointList();
        if (!_localAttached)
        {
            _localAttached = true;
            await LocalPane.AttachAsync(new LocalFileSource(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        if (_remote is null && EndpointCombo.SelectedItem is MachineProfile { IsOnline: true } or MachineProfile { IsConnected: true })
            await OpenRemoteAsync();
    }

    private void RefreshEndpointList()
    {
        if (_ctx is null) return;
        var current = EndpointCombo.SelectedItem as MachineProfile;
        var list = _ctx.AgentEndpoints.ToList();
        EndpointCombo.ItemsSource = list;
        var focused = _ctx.FocusedEndpoint();
        EndpointCombo.SelectedItem =
            current is not null && list.Contains(current) ? current :
            focused is not null && list.Contains(focused) ? focused :
            list.FirstOrDefault(m => m.IsConnected) ?? list.FirstOrDefault(m => m.IsOnline) ?? list.FirstOrDefault();
    }

    private void OnEndpointChanged(object sender, SelectionChangedEventArgs e) { }

    private async void OnOpenEndpointClick(object sender, RoutedEventArgs e) => await OpenRemoteAsync();

    private async Task OpenRemoteAsync()
    {
        if (_ctx is null || EndpointCombo.SelectedItem is not MachineProfile m) return;
        _remote = new RemoteFileSource(_ctx.Pool, m);
        RemotePane.Title = $"Remote — {m.DisplayTitle} ({m.Address})";
        SubtitleText.Text = $"Connected to {m.DisplayTitle}. Every file operation is written to the agent's audit log.";
        await RemotePane.AttachAsync(_remote);
    }

    // ---------------- Upload / download entry points ----------------
    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        var paths = LocalPane.SelectedRows.Select(r => r.FullPath).ToList();
        if (paths.Count == 0) { LocalPane.ShowError("Select local files or folders to upload."); return; }
        await UploadAsync(paths);
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var rows = RemotePane.SelectedRows;
        if (rows.Count == 0) { RemotePane.ShowError("Select remote files or folders to download."); return; }
        await DownloadAsync(rows.Select(r => (r.FullPath, r.Name, r.IsDir)).ToList());
    }

    private async void OnPaneDrop(FilePaneView target, IDataObject data)
    {
        if (target == RemotePane && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            await UploadAsync(files);
        }
        else if (target == LocalPane && data.GetData(FilePaneView.RemotePathsFormat) is string[] remotePaths && remotePaths.Length > 0)
        {
            var byPath = RemotePane.Rows.ToDictionary(r => r.FullPath, StringComparer.OrdinalIgnoreCase);
            await DownloadAsync(remotePaths
                .Select(p => byPath.TryGetValue(p, out var r) ? (r.FullPath, r.Name, r.IsDir) : (p, Path.GetFileName(p), false))
                .ToList());
        }
    }

    // ---------------- Upload ----------------
    private async Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (_ctx is null || _remote is null || RemotePane.CurrentPath.Length == 0)
        {
            RemotePane.ShowError("Open a remote endpoint and folder first.");
            return;
        }
        var remote = _remote;
        var remoteDir = RemotePane.CurrentPath;

        var existing = RemotePane.Rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clashes = localPaths.Select(Path.GetFileName).Where(n => n is not null && existing.Contains(n)).ToList();
        var overwrite = false;
        if (clashes.Count > 0)
        {
            var answer = MessageBox.Show(Window.GetWindow(this)!,
                $"{clashes.Count} item(s) already exist in {remoteDir}:\n\n{string.Join("\n", clashes.Take(8).Select(c => "  • " + c))}\n\nOverwrite existing files? (No = skip them)",
                "Home RMS — Upload", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            overwrite = answer == MessageBoxResult.Yes;
        }

        var jobs = new List<Task>();
        foreach (var path in localPaths)
        {
            try { await QueueUploadAsync(remote, path, remoteDir, overwrite, jobs); }
            catch (Exception ex) { RemotePane.ShowError($"Upload of {Path.GetFileName(path)} failed: {AgentErrors.Describe(ex)}"); }
        }
        await Task.WhenAll(jobs);
        if (_remote == remote && RemotePane.CurrentPath == remoteDir) await RemotePane.RefreshAsync();
    }

    private async Task QueueUploadAsync(RemoteFileSource remote, string localPath, string remoteDir, bool overwrite, List<Task> jobs)
    {
        var name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
        var dest = remote.Combine(remoteDir, name);
        if (Directory.Exists(localPath))
        {
            try { await remote.CreateDirectoryAsync(dest, CancellationToken.None); }
            catch (Exception ex) when (RemoteFileSource.IsAlreadyExists(ex) || ex is IOException) { /* already there: merge into it */ }
            foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
                await QueueUploadAsync(remote, child, dest, overwrite, jobs);
            return;
        }

        var item = new TransferItem { IsUpload = true, Name = name, SourcePath = localPath, DestinationPath = dest };
        _transfers.Insert(0, item);
        jobs.Add(RunTransferAsync(item, ct => UploadFileAsync(remote, item, overwrite, ct)));
    }

    private async Task UploadFileAsync(RemoteFileSource remote, TransferItem item, bool overwrite, CancellationToken ct)
    {
        try
        {
            var conn = await _ctx!.Pool.GetPairedAsync(remote.Endpoint, interactive: true, ct);
            await using var fs = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            item.Total = fs.Length;
            using var call = conn.Client.UploadFile(cancellationToken: ct);
            var buffer = new byte[ChunkSize];
            var first = true;
            while (true)
            {
                var n = await fs.ReadAsync(buffer, ct);
                if (n == 0 && !first) break;
                var chunk = new FileUploadChunk { Data = ByteString.CopyFrom(buffer, 0, n) };
                if (first)
                {
                    chunk.DestinationPath = item.DestinationPath;
                    chunk.Overwrite = overwrite;
                    chunk.TotalSize = fs.Length;
                    first = false;
                }
                await call.RequestStream.WriteAsync(chunk, ct);
                item.Done += n;
                if (n == 0) break;
            }
            await call.RequestStream.CompleteAsync();
            var res = await call.ResponseAsync;
            if (!res.Success) throw new IOException(res.Message);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            _ctx!.Pool.MarkUnpaired(remote.Endpoint.Host, remote.Endpoint.Port);
            throw;
        }
    }

    // ---------------- Download ----------------
    private async Task DownloadAsync(IReadOnlyList<(string FullPath, string Name, bool IsDir)> items)
    {
        if (_ctx is null || _remote is null) return;
        var localDir = LocalPane.CurrentPath;
        if (localDir.Length == 0) { LocalPane.ShowError("Open a local folder first."); return; }
        var remote = _remote;

        var clashes = items.Where(i => File.Exists(Path.Combine(localDir, i.Name)) || Directory.Exists(Path.Combine(localDir, i.Name))).ToList();
        var overwrite = false;
        if (clashes.Count > 0)
        {
            var answer = MessageBox.Show(Window.GetWindow(this)!,
                $"{clashes.Count} item(s) already exist in {localDir}:\n\n{string.Join("\n", clashes.Take(8).Select(c => "  • " + c.Name))}\n\nOverwrite existing files? (No = skip them)",
                "Home RMS — Download", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            overwrite = answer == MessageBoxResult.Yes;
        }

        var jobs = new List<Task>();
        foreach (var it in items)
        {
            try { await QueueDownloadAsync(remote, it.FullPath, it.Name, it.IsDir, localDir, overwrite, jobs); }
            catch (Exception ex) { LocalPane.ShowError($"Download of {it.Name} failed: {AgentErrors.Describe(ex)}"); }
        }
        await Task.WhenAll(jobs);
        if (LocalPane.CurrentPath == localDir) await LocalPane.RefreshAsync();
    }

    private async Task QueueDownloadAsync(RemoteFileSource remote, string remotePath, string name, bool isDir, string localDir, bool overwrite, List<Task> jobs)
    {
        var dest = Path.Combine(localDir, name);
        if (isDir)
        {
            Directory.CreateDirectory(dest);
            var listing = await remote.ListAsync(remotePath, CancellationToken.None);
            foreach (var e in listing.Entries)
                await QueueDownloadAsync(remote, e.FullPath, e.Name, e.IsDir, dest, overwrite, jobs);
            return;
        }

        var item = new TransferItem { IsUpload = false, Name = name, SourcePath = remotePath, DestinationPath = dest };
        _transfers.Insert(0, item);
        if (File.Exists(dest) && !overwrite)
        {
            item.IsActive = false;
            item.Status = "Skipped (already exists)";
            return;
        }
        jobs.Add(RunTransferAsync(item, ct => DownloadFileAsync(remote, item, ct)));
    }

    private async Task DownloadFileAsync(RemoteFileSource remote, TransferItem item, CancellationToken ct)
    {
        var conn = await _ctx!.Pool.GetPairedAsync(remote.Endpoint, interactive: true, ct);
        var tmp = item.DestinationPath + ".hrmspart";
        try
        {
            using (var call = conn.Client.DownloadFile(new FilePathRequest { Path = item.SourcePath }, cancellationToken: ct))
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct))
                {
                    if (chunk.TotalSize > 0 && item.Total != chunk.TotalSize) item.Total = chunk.TotalSize;
                    await fs.WriteAsync(chunk.Data.Memory, ct);
                    item.Done += chunk.Data.Length;
                }
            }
            File.Move(tmp, item.DestinationPath, overwrite: true);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            _ctx.Pool.MarkUnpaired(remote.Endpoint.Host, remote.Endpoint.Port);
            try { File.Delete(tmp); } catch { }
            throw;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // ---------------- Transfer runner ----------------
    private async Task RunTransferAsync(TransferItem item, Func<CancellationToken, Task> work)
    {
        try
        {
            await _transferSlots.WaitAsync(item.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            item.IsActive = false;
            item.Status = "Cancelled";
            return;
        }

        try
        {
            item.Status = item.IsUpload ? "Uploading…" : "Downloading…";
            await work(item.Cts.Token);
            item.Status = "Done";
        }
        catch (Exception ex) when (item.Cts.IsCancellationRequested)
        {
            _ = ex;
            item.Status = "Cancelled";
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            item.Status = "Skipped (already exists)";
        }
        catch (Exception ex)
        {
            item.IsError = true;
            item.Status = AgentErrors.Describe(ex);
            _ctx?.Log("ALERT", $"File transfer {item.Name} failed: {item.Status}");
        }
        finally
        {
            item.IsActive = false;
            _transferSlots.Release();
        }
    }

    private void OnCancelTransferClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TransferItem t }) t.Cts.Cancel();
    }

    private void OnCancelAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var t in _transfers.Where(t => t.IsActive)) t.Cts.Cancel();
    }

    private void OnClearFinishedClick(object sender, RoutedEventArgs e)
    {
        foreach (var t in _transfers.Where(t => !t.IsActive).ToList()) _transfers.Remove(t);
    }
}
