using System.Net;
using System.Text.Json;
using HomeRms.Controller.Services;

/// <summary>Remote-desktop quality presets: preset -> ScreenRequest mapping and the LAN default rule (pure, no network).</summary>
internal static class StreamPresetChecks
{
    public static void Run(Action<bool, string> check)
    {
        // ---- preset -> request ----
        var lan = StreamPresets.ToRequest(StreamPresets.Settings(StreamPreset.LanHighQuality), "op", monitorIndex: 0);
        check(lan is { Fps: 60, Quality: 95, MaxWidth: 0, Chroma: 2, LosslessRefine: true, RefineDelayMs: 120, SupportsTiles: true, OperatorName: "op", MonitorIndex: 0 },
            $"LAN High Quality -> 60 fps, q95, native, 4:4:4, lossless refine, 120 ms ({lan})");

        var bal = StreamPresets.ToRequest(StreamPresets.Settings(StreamPreset.Balanced), "op", monitorIndex: 1);
        check(bal is { Fps: 30, Quality: 85, MaxWidth: 0, Chroma: 2, LosslessRefine: false, RefineDelayMs: 0, MonitorIndex: 1 },
            $"Balanced -> 30 fps, q85, native, 4:4:4, no lossless ({bal})");

        var low = StreamPresets.ToRequest(StreamPresets.Settings(StreamPreset.LowBandwidth), "op", monitorIndex: 0);
        check(low is { Fps: 15, Quality: 65, MaxWidth: 1280, Chroma: 1, LosslessRefine: false },
            $"Low Bandwidth -> 15 fps, q65, max 1280 px, 4:2:0 ({low})");

        var custom = StreamPresets.ToRequest(StreamPresets.Settings(StreamPreset.Custom, 45, 70, 1920), "op", 0);
        check(custom is { Fps: 45, Quality: 70, MaxWidth: 1920, Chroma: 0, LosslessRefine: false, RefineDelayMs: 0 },
            "Custom uses the FPS / JPEG / resolution fields with agent-default chroma and no lossless refine");
        var clamped = StreamPresets.Settings(StreamPreset.Custom, 500, 5, -3);
        check(clamped is { Fps: 120, Quality: 20, MaxWidth: 0 }, "Custom values are clamped (fps <= 120, quality >= 20, width >= 0)");
        check(StreamPresets.ToRequest(StreamPresets.Settings(StreamPreset.Balanced), "op", 1, supportsTiles: false).SupportsTiles == false,
            "secondary-window requests can opt out of tiles");

        // ---- LAN detection ----
        IReadOnlyList<LocalSubnet> subnets =
        [
            new(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0")),
            new(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("255.255.0.0")),
        ];
        check(StreamPresets.DefaultFor(IPAddress.Parse("192.168.1.57"), "192.168.1.57", subnets) == StreamPreset.LanHighQuality, "same /24 -> LAN High Quality");
        check(StreamPresets.DefaultFor(IPAddress.Parse("10.0.200.9"), "nas", subnets) == StreamPreset.LanHighQuality, "inside a /16 -> LAN High Quality");
        check(StreamPresets.DefaultFor(IPAddress.Parse("::ffff:192.168.1.9"), "pc", subnets) == StreamPreset.LanHighQuality, "IPv4-mapped IPv6 on the LAN -> LAN High Quality");
        check(StreamPresets.DefaultFor(IPAddress.Loopback, "127.0.0.1", subnets) == StreamPreset.LanHighQuality, "loopback address -> LAN High Quality");
        check(StreamPresets.DefaultFor(null, "localhost", subnets) == StreamPreset.LanHighQuality, "'localhost' (not resolved yet) -> LAN High Quality");
        check(StreamPresets.DefaultFor(IPAddress.Parse("fe80::1c2d:3e4f:5a6b:7c8d"), "pc", subnets) == StreamPreset.LanHighQuality, "IPv6 link-local -> LAN High Quality");
        check(StreamPresets.DefaultFor(IPAddress.Parse("192.168.2.57"), "192.168.2.57", subnets) == StreamPreset.Balanced, "neighbouring /24 (routed) -> Balanced");
        check(StreamPresets.DefaultFor(IPAddress.Parse("100.64.3.7"), "pc.tailnet", subnets) == StreamPreset.Balanced, "VPN / CGNAT address -> Balanced");
        check(StreamPresets.DefaultFor(IPAddress.Parse("2001:db8::7"), "pc", subnets) == StreamPreset.Balanced, "global IPv6 -> Balanced");
        check(StreamPresets.DefaultFor(null, "pc.example.com", subnets) == StreamPreset.Balanced, "unknown address -> Balanced");
        check(StreamPresets.DefaultFor(IPAddress.Parse("192.168.1.57"), "x", []) == StreamPreset.Balanced, "no local subnets known -> Balanced");

        // ---- saved choice wins, bad values fall back ----
        check(StreamPresets.Resolve("LowBandwidth", IPAddress.Parse("192.168.1.57"), "pc", subnets) == StreamPreset.LowBandwidth, "saved choice beats the LAN default");
        check(StreamPresets.Resolve("lanhighquality", IPAddress.Parse("8.8.8.8"), "pc", subnets) == StreamPreset.LanHighQuality, "saved choice beats the non-LAN default (case-insensitive)");
        foreach (var bad in new string?[] { null, "", "Turbo", "7", "2" })
            check(StreamPresets.Resolve(bad, IPAddress.Parse("8.8.8.8"), "pc", subnets) == StreamPreset.Balanced, $"invalid saved value '{bad}' -> network default");

        check(StreamPresets.EndpointKey("STUDY-PC", "192.168.1.57", 7443) == "study-pc" &&
              StreamPresets.EndpointKey("", "Kitchen-Laptop", 7443) == "kitchen-laptop:7443",
            "endpoint key: computer name, else host:port, lower-case");

        // ---- persisted in ui-settings.json ----
        var ui = new UiSettings();
        ui.StreamPresetByEndpoint["study-pc"] = StreamPreset.Custom.ToString();
        var back = JsonSerializer.Deserialize<UiSettings>(JsonSerializer.Serialize(ui))!;
        check(back.StreamPresetByEndpoint.TryGetValue("study-pc", out var v) && StreamPresets.TryParse(v, out var p) && p == StreamPreset.Custom,
            "per-endpoint preset survives the ui-settings.json round trip");
        check(JsonSerializer.Deserialize<UiSettings>("{\"ConsoleOpen\":true}")!.StreamPresetByEndpoint.Count == 0,
            "older ui-settings.json without presets loads with an empty map");

        foreach (var preset in StreamPresets.All)
            check(StreamPresets.Label(preset).Length > 0 && StreamPresets.Description(preset).Length > 0, $"label + description for {preset}");
    }
}
