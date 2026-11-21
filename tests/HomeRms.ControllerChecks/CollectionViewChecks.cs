using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using HomeRms.Controller.Views;

/// <summary>
/// Regression checks for the Services &amp; scheduled tasks crash:
/// "Cannot change or check the contents or Current position of CollectionView while Refresh is being deferred."
/// Reproduces the old refill pattern (Clear/Add inside DeferRefresh with a SelectionChanged handler that
/// reads SelectedItem) and verifies the BulkObservableCollection.ReplaceAll path the view uses now.
/// </summary>
internal static class CollectionViewChecks
{
    private sealed record Row(string Name, string State);

    public static void Run(Action<bool, string> check)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { RunSta(check); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        check(error is null, "CollectionView checks ran on an STA thread" + (error is null ? "" : " -> " + error));
    }

    private static void RunSta(Action<bool, string> check)
    {
        var initial = new[] { new Row("Spooler", "Running"), new Row("BITS", "Stopped"), new Row("WinRM", "Running") };
        var reloaded = new[] { new Row("wuauserv", "Running"), new Row("Spooler", "Stopped"), new Row("Audiosrv", "Running"), new Row("BITS", "Running") };

        // ---- old pattern: reproduces the crash ----
        {
            var items = new ObservableCollection<Row>(initial);
            var (grid, view) = MakeGrid(items, "");
            grid.SelectedItem = items[0];
            Exception? caught = null;
            try
            {
                using (view.DeferRefresh())
                {
                    items.Clear();
                    foreach (var r in reloaded) items.Add(r);
                }
            }
            catch (InvalidOperationException ex) { caught = ex; }
            check(caught?.Message.Contains("Refresh is being deferred", StringComparison.Ordinal) == true,
                "old refill (Clear/Add inside DeferRefresh + SelectionChanged reading SelectedItem) reproduces the reported exception" +
                (caught is null ? " (no exception — the repro no longer matches)" : ""));
        }

        // ---- new pattern: single Reset, selection restored, sort + filter kept ----
        {
            var items = new BulkObservableCollection<Row>();
            items.ReplaceAll(initial);
            var filter = "";
            var (grid, view) = MakeGrid(items, null, () => filter);
            grid.SelectedItem = items.First(r => r.Name == "Spooler");

            var keep = grid.SelectedItems.Cast<Row>().Select(r => r.Name).ToHashSet();
            Exception? caught = null;
            var resets = 0;
            items.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
            try
            {
                grid.UnselectAll();
                items.ReplaceAll(reloaded);
                foreach (var r in items.Where(r => keep.Contains(r.Name))) grid.SelectedItems.Add(r);
            }
            catch (Exception ex) { caught = ex; }
            check(caught is null, "ReplaceAll refill does not throw" + (caught is null ? "" : " -> " + caught.Message));
            check(resets == 1, $"ReplaceAll raises exactly one Reset (got {resets})");
            check(view.Cast<Row>().Select(r => r.Name).SequenceEqual(["Audiosrv", "BITS", "Spooler", "wuauserv"]), "sort order survives the refill");
            check(grid.SelectedItem is Row { Name: "Spooler" }, "selection is restored by key after the refill");

            // typing in the filter box between / after refills
            filter = "run";
            caught = null;
            try { view.Refresh(); items.ReplaceAll(reloaded.Reverse()); view.Refresh(); }
            catch (Exception ex) { caught = ex; }
            check(caught is null, "filter refresh around a refill does not throw" + (caught is null ? "" : " -> " + caught.Message));
            check(view.Cast<Row>().All(r => r.State == "Running") && view.Cast<Row>().Count() == 3, "filter still applies after the refill");
        }
    }

    private static (DataGrid Grid, ICollectionView View) MakeGrid<T>(ObservableCollection<T> items, string? _, Func<string>? filter = null) where T : notnull
    {
        var view = new ListCollectionView(items);
        view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
        if (filter is not null)
            view.Filter = o => o is Row r && (filter().Length == 0 || r.State.Contains(filter(), StringComparison.OrdinalIgnoreCase));
        var grid = new DataGrid { ItemsSource = view, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, IsSynchronizedWithCurrentItem = true };
        string? last = null;
        // Same shape as ServicesTasksView.OnServiceSelectionChanged: read the selection when it changes
        grid.SelectionChanged += (_, _) => last = grid.SelectedItem?.ToString();
        _ = last;
        return (grid, view);
    }
}
