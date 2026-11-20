using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views;

public sealed class TargetPick(MachineProfile profile) : INotifyPropertyChanged
{
    private bool _isChecked;
    public MachineProfile Profile { get; } = profile;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; Raise(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>Saved snippets (persisted in script-library.json) + multi-endpoint execution.</summary>
public partial class ScriptLibraryView : UserControl
{
    private readonly ObservableCollection<ScriptSnippet> _snippets = new();
    private readonly ObservableCollection<TargetPick> _targets = new();
    private readonly ObservableCollection<ScriptRun> _runs = new();
    private FeatureContext? _ctx;
    private ScriptSnippet? _editing;
    private ScriptRun? _watchedRun;
    private CancellationTokenSource? _runCts;

    public ScriptLibraryView()
    {
        InitializeComponent();
        foreach (var s in ScriptLibraryStore.Load()) _snippets.Add(s);
        SnippetList.ItemsSource = _snippets;
        TargetsList.ItemsSource = _targets;
        RunsGrid.ItemsSource = _runs;
        if (_snippets.Count > 0) SnippetList.SelectedIndex = 0;
    }

    public void Initialize(FeatureContext ctx)
    {
        _ctx = ctx;
        RefreshTargets();
    }

    public void OnShown()
    {
        RefreshTargets();
        // Pre-tick the Endpoints multi-selection the first time nothing is ticked.
        if (!_targets.Any(t => t.IsChecked)) PickFleetSelection();
    }

    private void RefreshTargets()
    {
        if (_ctx is null) return;
        var ticked = _targets.Where(t => t.IsChecked).Select(t => t.Profile).ToHashSet();
        _targets.Clear();
        foreach (var m in _ctx.Machines.Where(m => m.IsWindows))
            _targets.Add(new TargetPick(m) { IsChecked = ticked.Contains(m) });
        UpdateRunButton();
    }

    private void UpdateRunButton()
    {
        var n = _targets.Count(t => t.IsChecked);
        RunBtn.Content = n == 0 ? "Run" : $"Run on {n} endpoint(s)";
    }

    private void OnTargetToggled(object sender, RoutedEventArgs e) => UpdateRunButton();

    private void PickFleetSelection()
    {
        if (_ctx is null) return;
        var sel = _ctx.SelectedEndpoints().ToHashSet();
        foreach (var t in _targets) t.IsChecked = sel.Contains(t.Profile);
        UpdateRunButton();
    }

    private void OnPickFleetSelectionClick(object sender, RoutedEventArgs e) => PickFleetSelection();

    private void OnPickOnlineClick(object sender, RoutedEventArgs e)
    {
        foreach (var t in _targets) t.IsChecked = t.Profile.IsOnline || t.Profile.IsConnected;
        UpdateRunButton();
    }

    // ---------------- Library ----------------
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        SnippetList.ItemsSource = q.Length == 0
            ? _snippets
            : _snippets.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                   s.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                   s.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void OnSnippetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SnippetList.SelectedItem is ScriptSnippet s) LoadEditor(s);
    }

    private void LoadEditor(ScriptSnippet s)
    {
        _editing = s;
        NameBox.Text = s.Name;
        DescriptionBox.Text = s.Description;
        TagsBox.Text = s.TagsText;
        ShellCombo.SelectedIndex = s.Shell == ScriptShell.Cmd ? 1 : 0;
        BodyBox.Text = s.Body;

        var ro = s.IsBuiltIn;
        NameBox.IsReadOnly = DescriptionBox.IsReadOnly = TagsBox.IsReadOnly = BodyBox.IsReadOnly = ro;
        ShellCombo.IsEnabled = !ro;
        SaveBtn.IsEnabled = RevertBtn.IsEnabled = !ro;
        EditorNote.Text = ro ? "Built-in snippet (read-only). Use Duplicate to make an editable copy." : "";
    }

    private void Persist() => ScriptLibraryStore.Save(_snippets);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_editing is null || _editing.IsBuiltIn) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { EditorNote.Text = "Enter a name."; return; }
        if (BodyBox.Text.Trim().Length == 0) { EditorNote.Text = "The script body is empty."; return; }

        var updated = _editing.Clone();
        updated.Name = name;
        updated.Description = DescriptionBox.Text.Trim();
        updated.Tags = TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        updated.Shell = ShellCombo.SelectedIndex == 1 ? ScriptShell.Cmd : ScriptShell.PowerShell;
        updated.Body = BodyBox.Text;

        var idx = _snippets.IndexOf(_editing);
        if (idx < 0) _snippets.Add(updated);
        else _snippets[idx] = updated;
        Persist();
        OnSearchChanged(SearchBox, null!);
        SnippetList.SelectedItem = updated;
        EditorNote.Text = $"Saved {DateTime.Now:HH:mm:ss}.";
    }

    private void OnRevertClick(object sender, RoutedEventArgs e)
    {
        if (_editing is not null) LoadEditor(_editing);
    }

    private void AddAndSelect(ScriptSnippet s)
    {
        _snippets.Add(s);
        Persist();
        SearchBox.Text = "";
        SnippetList.SelectedItem = s;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnNewClick(object sender, RoutedEventArgs e) =>
        AddAndSelect(new ScriptSnippet { Name = "New script", Body = "Get-ComputerInfo | Select-Object CsName, OsName, OsVersion", Tags = ["custom"] });

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        var copy = _editing.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.IsBuiltIn = false;
        copy.Name = _editing.Name + " (copy)";
        AddAndSelect(copy);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        if (_editing.IsBuiltIn) { EditorNote.Text = "Built-in snippets cannot be deleted."; return; }
        if (MessageBox.Show(Window.GetWindow(this)!, $"Delete the snippet '{_editing.Name}'?", "Home RMS — Script Library",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _snippets.Remove(_editing);
        Persist();
        OnSearchChanged(SearchBox, null!);
        SnippetList.SelectedIndex = SnippetList.Items.Count > 0 ? 0 : -1;
    }

    // ---------------- Run ----------------
    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_ctx is null || _runCts is not null) return;
        var targets = _targets.Where(t => t.IsChecked).Select(t => t.Profile).ToList();
        if (targets.Count == 0) { RunCaption.Text = "RESULTS — tick at least one target endpoint"; return; }

        var body = BodyBox.Text;
        if (body.Trim().Length == 0) { RunCaption.Text = "RESULTS — the script body is empty"; return; }
        var shell = ShellCombo.SelectedIndex == 1 ? ScriptShell.Cmd : ScriptShell.PowerShell;
        var title = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "Ad-hoc script";
        var minutes = double.TryParse(TimeoutBox.Text, out var t) ? Math.Clamp(t, 1, 240) : 15;

        var names = string.Join("\n", targets.Take(12).Select(m => "  • " + m.DisplayTitle));
        if (targets.Count > 12) names += $"\n  … and {targets.Count - 12} more";
        if (MessageBox.Show(Window.GetWindow(this)!,
                $"Run '{title}' ({(shell == ScriptShell.Cmd ? "cmd" : "PowerShell")}) on {targets.Count} endpoint(s)?\n\n{names}",
                "Home RMS — Run Script", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunAsync(targets, shell, body, title, TimeSpan.FromMinutes(minutes));
    }

    /// <summary>Runs a script on the given targets and shows the per-endpoint results in this view.</summary>
    public async Task RunAsync(IReadOnlyList<MachineProfile> targets, ScriptShell shell, string body, string title, TimeSpan timeout)
    {
        if (_ctx is null || _runCts is not null) return;
        _runCts = new CancellationTokenSource();
        RunBtn.IsEnabled = false;
        CancelRunBtn.IsEnabled = true;
        _runs.Clear();
        WatchRun(null);

        _ctx.Log("SCRIPT", $"Running '{title}' on {targets.Count} endpoint(s).");
        var (runs, completion) = _ctx.Scripts.Start(targets, shell, body, title, timeout, _runCts.Token);
        foreach (var r in runs) _runs.Add(r);
        RunsGrid.SelectedIndex = 0;
        RunCaption.Text = $"RESULTS — {title}";
        try
        {
            await completion;
        }
        finally
        {
            var ok = runs.Count(r => r.State == ScriptRunState.Succeeded);
            RunCaption.Text = $"RESULTS — {title}: {ok}/{runs.Count} succeeded";
            _ctx.Log(ok == runs.Count ? "SCRIPT" : "ALERT", $"'{title}' finished: {ok}/{runs.Count} succeeded.");
            _runCts.Dispose();
            _runCts = null;
            RunBtn.IsEnabled = true;
            CancelRunBtn.IsEnabled = false;
        }
    }

    private void OnCancelRunClick(object sender, RoutedEventArgs e) => _runCts?.Cancel();

    private void OnRunSelected(object sender, SelectionChangedEventArgs e) => WatchRun(RunsGrid.SelectedItem as ScriptRun);

    private void WatchRun(ScriptRun? run)
    {
        if (_watchedRun is not null) _watchedRun.PropertyChanged -= OnWatchedRunChanged;
        _watchedRun = run;
        if (run is not null) run.PropertyChanged += OnWatchedRunChanged;
        ShowOutput();
    }

    private void OnWatchedRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScriptRun.Output) or nameof(ScriptRun.State)) ShowOutput();
    }

    private void ShowOutput()
    {
        if (_watchedRun is null)
        {
            OutputBox.Text = "Select a result row to see that endpoint's output.";
            return;
        }
        var text = _watchedRun.Output;
        OutputBox.Text = text.Length == 0 ? $"[{_watchedRun.StateText}] {_watchedRun.Summary}" : text;
        OutputBox.ScrollToEnd();
    }
}
