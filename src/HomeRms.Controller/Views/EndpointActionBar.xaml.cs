using System.Windows;
using System.Windows.Controls;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;

namespace HomeRms.Controller.Views;

/// <summary>
/// Power &amp; Wake-on-LAN action bar. Acts on every endpoint returned by <see cref="TargetsProvider"/>
/// (the multi-selection in Endpoints, or the endpoint picked in a feature view).
/// </summary>
public partial class EndpointActionBar : UserControl
{
    public EndpointActionBar()
    {
        InitializeComponent();
    }

    public FeatureContext? Context { get; set; }
    public Func<IReadOnlyList<MachineProfile>>? TargetsProvider { get; set; }

    private IReadOnlyList<MachineProfile> Targets() => TargetsProvider?.Invoke() ?? [];

    private void SetResult(string text, bool error)
    {
        ResultText.Text = text;
        ResultText.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "MutedBrush");
    }

    private bool EnsureMac(MachineProfile m)
    {
        if (PowerActions.NormalizeMac(m.MacAddress) is not null) return true;
        var mac = InputDialog.Prompt(this, "Wake-on-LAN",
            $"'{m.DisplayTitle}' has no MAC address saved. Enter it (e.g. 00-11-22-33-44-55) to enable Wake-on-LAN:",
            m.MacAddress, v => PowerActions.NormalizeMac(v) is null ? "Enter 12 hex digits (separators optional)." : null);
        if (mac is null) return false;
        m.MacAddress = PowerActions.NormalizeMac(mac)!;
        Context?.SaveMachines();
        return true;
    }

    private void OnEditMacClick(object sender, RoutedEventArgs e)
    {
        var targets = Targets();
        if (targets.Count != 1)
        {
            SetResult("Select exactly one endpoint to edit its MAC address.", error: true);
            return;
        }
        var m = targets[0];
        var mac = InputDialog.Prompt(this, "Wake-on-LAN MAC address", $"MAC address of '{m.DisplayTitle}':", m.MacAddress,
            v => v.Length == 0 || PowerActions.NormalizeMac(v) is not null ? null : "Enter 12 hex digits (separators optional).");
        if (mac is null) return;
        m.MacAddress = mac.Length == 0 ? "" : PowerActions.NormalizeMac(mac)!;
        Context?.SaveMachines();
        SetResult(mac.Length == 0 ? $"MAC address of {m.DisplayTitle} cleared." : $"MAC address of {m.DisplayTitle} set to {m.MacAddress}.", error: false);
    }

    private async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (Context is null || sender is not Button { Tag: string id } btn || PowerActions.Find(id) is not { } action) return;

        var targets = Targets();
        if (targets.Count == 0)
        {
            SetResult("Select one or more endpoints first.", error: true);
            return;
        }

        if (action == PowerActions.Wake)
        {
            targets = targets.Where(EnsureMac).ToList();
            if (targets.Count == 0) return;
        }

        if (action.Destructive)
        {
            var names = string.Join("\n", targets.Take(12).Select(t => $"  • {t.DisplayTitle} ({t.Address})"));
            if (targets.Count > 12) names += $"\n  … and {targets.Count - 12} more";
            if (MessageBox.Show(Window.GetWindow(this)!,
                    $"{action.Label}: this will {action.ConfirmVerb} {targets.Count} endpoint(s):\n\n{names}\n\nContinue?",
                    $"Home RMS — Confirm {action.Label}", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        btn.IsEnabled = false;
        SetResult($"{action.Label}: sending to {targets.Count} endpoint(s)…", error: false);
        var results = await Task.WhenAll(targets.Select(async m =>
        {
            try
            {
                var msg = await PowerActions.ExecuteAsync(action, m, Context.Pool, Context.Agentless);
                Context.Log("POWER", $"{action.Label} → {m.DisplayTitle}: {msg}");
                return (m, ok: true, msg);
            }
            catch (Exception ex)
            {
                var msg = AgentErrors.Describe(ex);
                Context.Log("ALERT", $"{action.Label} failed for {m.DisplayTitle}: {msg}");
                return (m, ok: false, msg);
            }
        }));
        btn.IsEnabled = true;

        var failed = results.Where(r => !r.ok).ToList();
        if (failed.Count == 0)
            SetResult(targets.Count == 1 ? $"{action.Label}: {results[0].msg}" : $"{action.Label}: OK on {targets.Count} endpoint(s).", error: false);
        else if (failed.Count == 1)
            SetResult($"{action.Label} failed on {failed[0].m.DisplayTitle}: {failed[0].msg}", error: true);
        else
            SetResult($"{action.Label} failed on {failed.Count}/{targets.Count} endpoints (see audit log).", error: true);
    }
}
