using System.Collections.ObjectModel;
using HomeRms.Controller.Models;

namespace HomeRms.Controller.Services;

/// <summary>Everything the feature views (Views\*.xaml) need from the main window.</summary>
public sealed class FeatureContext
{
    public required ObservableCollection<MachineProfile> Machines { get; init; }
    public required AgentConnectionPool Pool { get; init; }
    public required AlertCenterService Alerts { get; init; }
    public required AgentlessService Agentless { get; init; }
    public required TelemetryHub Telemetry { get; init; }
    public required ScriptExecutor Scripts { get; init; }

    /// <summary>Writes to the Central Hub audit log (category, message). Thread-safe.</summary>
    public required Action<string, string> Log { get; init; }

    /// <summary>Persists the machine list (e.g. after a MAC address was added).</summary>
    public required Action SaveMachines { get; init; }

    /// <summary>The endpoint the operator is working with in the main window (connected or selected).</summary>
    public required Func<MachineProfile?> FocusedEndpoint { get; init; }

    /// <summary>All endpoints currently selected in the Endpoints view (multi-select).</summary>
    public required Func<IReadOnlyList<MachineProfile>> SelectedEndpoints { get; init; }

    /// <summary>Windows endpoints that can run agent RPCs.</summary>
    public IEnumerable<MachineProfile> AgentEndpoints => Machines.Where(m => m.Kind == EndpointKind.WindowsAgent);
}
