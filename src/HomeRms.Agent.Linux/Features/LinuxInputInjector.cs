using System.Diagnostics;
using HomeRms.Shared.Protocol;

namespace HomeRms.Agent.Linux.Features;

/// <summary>Injects mouse and keyboard events on Linux using xdotool (X11) when available.</summary>
public static class LinuxInputInjector
{
    private static readonly bool HasXdotool = CheckXdotool();

    public static void Apply(InputEvent e, int screenWidth, int screenHeight)
    {
        if (!HasXdotool) return;
        var px = (int)Math.Round(e.X / 65535.0 * Math.Max(1, screenWidth));
        var py = (int)Math.Round(e.Y / 65535.0 * Math.Max(1, screenHeight));
        var btn = e.Button switch { 1 => 3, 2 => 2, _ => 1 };

        switch (e.Kind)
        {
            case InputEvent.Types.Kind.MouseMove:
                Run($"mousemove {px} {py}");
                break;
            case InputEvent.Types.Kind.MouseDown:
                Run($"mousemove {px} {py} mousedown {btn}");
                break;
            case InputEvent.Types.Kind.MouseUp:
                Run($"mousemove {px} {py} mouseup {btn}");
                break;
            case InputEvent.Types.Kind.MouseWheel:
                var dir = e.Wheel > 0 ? 4 : 5;
                Run($"click {dir}");
                break;
            case InputEvent.Types.Kind.KeyDown:
                if (MapKey(e.KeyCode) is { } kd) Run($"keydown {kd}");
                break;
            case InputEvent.Types.Kind.KeyUp:
                if (MapKey(e.KeyCode) is { } ku) Run($"keyup {ku}");
                break;
        }
    }

    private static string? MapKey(int vk) => vk switch
    {
        0x08 => "BackSpace",
        0x09 => "Tab",
        0x0D => "Return",
        0x10 => "Shift_L",
        0x11 => "Control_L",
        0x12 => "Alt_L",
        0x1B => "Escape",
        0x20 => "space",
        0x21 => "Prior",
        0x22 => "Next",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)(vk + 32)).ToString(),
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        _ => null
    };

    private static bool CheckXdotool()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "xdotool",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(1000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "xdotool",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(200);
        }
        catch { }
    }
}
