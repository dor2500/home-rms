using System.Net;
using System.Net.Sockets;
using HomeRms.Shared.Network;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Services;

/// <summary>Remote-desktop quality presets shown in the Remote desktop toolbar.</summary>
public enum StreamPreset
{
    LanHighQuality = 0,
    Balanced = 1,
    LowBandwidth = 2,
    Custom = 3,
}

/// <summary>What one stream asks the agent for (maps 1:1 onto <see cref="ScreenRequest"/>).</summary>
/// <param name="Chroma">0 = agent default, 1 = 4:2:0, 2 = 4:4:4.</param>
/// <param name="MaxWidth">0 = native resolution.</param>
/// <param name="RefineDelayMs">0 = agent default.</param>
public sealed record StreamSettings(int Fps, int Quality, int MaxWidth, int Chroma, bool LosslessRefine, int RefineDelayMs);

/// <summary>Pure preset rules (unit-tested in ControllerChecks). No UI, no I/O.</summary>
public static class StreamPresets
{
    public const int ChromaDefault = 0;
    public const int Chroma420 = 1;
    public const int Chroma444 = 2;

    public static readonly StreamPreset[] All = [StreamPreset.LanHighQuality, StreamPreset.Balanced, StreamPreset.LowBandwidth, StreamPreset.Custom];

    public static string Label(StreamPreset p) => p switch
    {
        StreamPreset.LanHighQuality => "LAN High Quality",
        StreamPreset.Balanced => "Balanced",
        StreamPreset.LowBandwidth => "Low Bandwidth",
        _ => "Custom",
    };

    /// <summary>Short form for the status line.</summary>
    public static string ShortLabel(StreamPreset p) => p switch
    {
        StreamPreset.LanHighQuality => "LAN HQ",
        StreamPreset.Balanced => "Balanced",
        StreamPreset.LowBandwidth => "Low BW",
        _ => "Custom",
    };

    public static string Description(StreamPreset p) => p switch
    {
        StreamPreset.LanHighQuality => "60 fps, JPEG 95, native resolution, 4:4:4 colour; still areas become pixel-exact (lossless) after 120 ms",
        StreamPreset.Balanced => "30 fps, JPEG 85, native resolution, 4:4:4 colour",
        StreamPreset.LowBandwidth => "15 fps, JPEG 65, at most 1280 px wide, 4:2:0 colour",
        _ => "Your own FPS / JPEG quality / resolution",
    };

    /// <summary>
    /// The request settings for a preset. <paramref name="custom"/> supplies fps / quality / max width for
    /// <see cref="StreamPreset.Custom"/> (chroma = agent default, no lossless refine: the pre-preset behaviour).
    /// </summary>
    public static StreamSettings Settings(StreamPreset p, int customFps = 30, int customQuality = 88, int customMaxWidth = 0) => p switch
    {
        StreamPreset.LanHighQuality => new(60, 95, 0, Chroma444, true, 120),
        StreamPreset.Balanced => new(30, 85, 0, Chroma444, false, 0),
        StreamPreset.LowBandwidth => new(15, 65, 1280, Chroma420, false, 0),
        _ => new(Math.Clamp(customFps, 1, 120), Math.Clamp(customQuality, 20, 100), Math.Max(0, customMaxWidth), ChromaDefault, false, 0),
    };

    public static ScreenRequest ToRequest(StreamSettings s, string operatorName, int monitorIndex, bool supportsTiles = true) => new()
    {
        Fps = s.Fps,
        Quality = s.Quality,
        OperatorName = operatorName,
        MaxWidth = s.MaxWidth,
        SupportsTiles = supportsTiles,
        MonitorIndex = monitorIndex,
        Chroma = s.Chroma,
        LosslessRefine = s.LosslessRefine,
        RefineDelayMs = s.RefineDelayMs,
    };

    /// <summary>
    /// True when the agent is on this controller's LAN: loopback, an IPv4 address inside one of the local
    /// subnets, or an IPv6 link-local address.
    /// </summary>
    public static bool IsLan(IPAddress? address, string? host, IReadOnlyList<LocalSubnet> localSubnets)
    {
        if (EndpointAddress.IsLoopbackHost(host)) return true;
        if (address is null) return false;
        var ip = RmsNetwork.Normalize(address);
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal;
        return localSubnets.Any(s => s.Contains(ip));
    }

    /// <summary>First-time default: LAN High Quality on the local network, Balanced anywhere else (VPN, hub relay, internet).</summary>
    public static StreamPreset DefaultFor(IPAddress? address, string? host, IReadOnlyList<LocalSubnet> localSubnets) =>
        IsLan(address, host, localSubnets) ? StreamPreset.LanHighQuality : StreamPreset.Balanced;

    /// <summary>The preset to use: the user's saved choice for this endpoint, else the network default.</summary>
    public static StreamPreset Resolve(string? saved, IPAddress? address, string? host, IReadOnlyList<LocalSubnet> localSubnets) =>
        TryParse(saved, out var p) ? p : DefaultFor(address, host, localSubnets);

    public static bool TryParse(string? value, out StreamPreset preset)
    {
        preset = StreamPreset.Balanced;
        return !string.IsNullOrWhiteSpace(value) && !int.TryParse(value, out _) &&
               Enum.TryParse(value, ignoreCase: true, out preset) && Enum.IsDefined(preset);
    }

    /// <summary>Settings key for one endpoint: its computer name when known, else host:port (case-insensitive).</summary>
    public static string EndpointKey(string? computerName, string host, int port) =>
        (!string.IsNullOrWhiteSpace(computerName) ? computerName.Trim() : $"{host.Trim()}:{port}").ToLowerInvariant();
}
