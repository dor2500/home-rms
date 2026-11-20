using HomeRms.Controller.Models;
using HomeRms.Shared.Network;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Services;

public sealed record PowerAction(string Id, string Label, bool Destructive, string ConfirmVerb);

/// <summary>Power / Wake-on-LAN actions for one or many endpoints (SystemOp "power" area).</summary>
public static class PowerActions
{
    public static readonly PowerAction Wake = new("wake", "Wake", false, "wake");
    public static readonly PowerAction Restart = new("restart", "Restart", true, "restart");
    public static readonly PowerAction Shutdown = new("shutdown", "Shut down", true, "shut down");
    public static readonly PowerAction Sleep = new("sleep", "Sleep", true, "put to sleep");
    public static readonly PowerAction Lock = new("lock", "Lock", false, "lock");
    public static readonly PowerAction Logoff = new("logoff", "Log off", true, "sign the user out of");
    public static readonly PowerAction Cancel = new("cancel", "Cancel shutdown", false, "cancel the pending shutdown on");

    public static readonly IReadOnlyList<PowerAction> All = [Wake, Restart, Shutdown, Sleep, Lock, Logoff, Cancel];

    public static PowerAction? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>Normalizes "aa-bb-cc-dd-ee-ff" / "aabb.ccdd.eeff" / "AABBCCDDEEFF"; null when invalid.</summary>
    public static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        return string.Join("-", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    /// <summary>Executes one action on one endpoint and returns a short result message (throws on failure).</summary>
    public static async Task<string> ExecuteAsync(
        PowerAction action, MachineProfile m, AgentConnectionPool pool, AgentlessService agentless, CancellationToken ct = default)
    {
        if (action == Wake)
        {
            var mac = NormalizeMac(m.MacAddress) ?? throw new InvalidOperationException("No valid MAC address saved for this endpoint.");
            await WakeOnLan.SendAsync(mac);
            return $"Magic packet sent to {mac}.";
        }

        var isLinuxAgent = m.Kind == EndpointKind.Linux && m.ConnectionMode == ConnectionMode.Agent;
        if (isLinuxAgent)
        {
            if (action.Id is not ("restart" or "shutdown" or "cancel"))
                throw new NotSupportedException($"'{action.Label}' is not supported on Linux agent endpoints.");
        }
        else if (m.IsAppliance)
        {
            throw new NotSupportedException("Power actions are only available for Windows and Linux agent endpoints.");
        }

        if (m.IsAgentless)
        {
            var cmd = action.Id switch
            {
                "restart" => "shutdown.exe /r /t 30 /c 'Home RMS: restart requested'",
                "shutdown" => "shutdown.exe /s /t 30 /c 'Home RMS: shutdown requested'",
                "cancel" => "shutdown.exe /a",
                "logoff" => "logoff.exe console",
                _ => throw new NotSupportedException($"'{action.Label}' needs the Home RMS agent (not available over WinRM).")
            };
            var (ok, output) = await agentless.RunShellAsync(m, cmd, ct);
            if (!ok) throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? "WinRM command failed." : output.Trim());
            return $"{action.Label} requested over WinRM.";
        }

        var res = await pool.CallAsync(m, interactive: true, c => c.SystemOpAsync(
            new SystemOpRequest { Area = "power", Action = action.Id, Target = "", Extra = "" },
            deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: ct).ResponseAsync, ct);
        if (!res.Success) throw new InvalidOperationException(res.Message);
        return res.Message;
    }
}
