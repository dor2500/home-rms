using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HomeRms.Shared.Protocol;

namespace HomeRms.Agent.Features;

/// <summary>Injects mouse and keyboard events into the local session via SendInput.</summary>
[SupportedOSPlatform("windows")]
public static class InputInjector
{
    public static void Apply(InputEvent e, int monitorIndex = 0)
    {
        var (vx, vy, vflag) = MapCoordinates(e.X, e.Y, monitorIndex);
        switch (e.Kind)
        {
            case InputEvent.Types.Kind.MouseMove:
                SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | vflag, vx, vy, 0);
                break;
            case InputEvent.Types.Kind.MouseDown:
                SendMouse(DownFlag(e.Button) | MOUSEEVENTF_ABSOLUTE | vflag, vx, vy, 0);
                break;
            case InputEvent.Types.Kind.MouseUp:
                SendMouse(UpFlag(e.Button) | MOUSEEVENTF_ABSOLUTE | vflag, vx, vy, 0);
                break;
            case InputEvent.Types.Kind.MouseWheel:
                SendMouse(MOUSEEVENTF_WHEEL, 0, 0, e.Wheel);
                break;
            case InputEvent.Types.Kind.KeyDown:
                SendKey((ushort)e.KeyCode, down: true);
                break;
            case InputEvent.Types.Kind.KeyUp:
                SendKey((ushort)e.KeyCode, down: false);
                break;
        }
    }

    private static (int X, int Y, uint Flag) MapCoordinates(int normX, int normY, int monitorIndex)
    {
        if (monitorIndex == 0)
            return (normX, normY, 0);

        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (vs.Width <= 0 || vs.Height <= 0)
            return (normX, normY, 0);

        if (monitorIndex == -1)
            return (normX, normY, MOUSEEVENTF_VIRTUALDESK);

        var screens = System.Windows.Forms.Screen.AllScreens;
        if (monitorIndex > 0 && monitorIndex < screens.Length)
        {
            var b = screens[monitorIndex].Bounds;
            var px = b.Left + (normX / 65535.0) * b.Width;
            var py = b.Top + (normY / 65535.0) * b.Height;
            var vx = (int)Math.Clamp(Math.Round(((px - vs.Left) / vs.Width) * 65535.0), 0, 65535);
            var vy = (int)Math.Clamp(Math.Round(((py - vs.Top) / vs.Height) * 65535.0), 0, 65535);
            return (vx, vy, MOUSEEVENTF_VIRTUALDESK);
        }

        return (normX, normY, 0);
    }

    private static uint DownFlag(int button) => button switch
    {
        1 => MOUSEEVENTF_RIGHTDOWN,
        2 => MOUSEEVENTF_MIDDLEDOWN,
        _ => MOUSEEVENTF_LEFTDOWN
    };

    private static uint UpFlag(int button) => button switch
    {
        1 => MOUSEEVENTF_RIGHTUP,
        2 => MOUSEEVENTF_MIDDLEUP,
        _ => MOUSEEVENTF_LEFTUP
    };

    private static void SendMouse(uint flags, int x, int y, int data)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(ushort vk, bool down)
    {
        var scan = (ushort)MapVirtualKey(vk, 0);
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        if (IsExtendedKey(vk))
            flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or // PageUp, PageDown, End, Home
        0x25 or 0x26 or 0x27 or 0x28 or // Arrow keys
        0x2D or 0x2E or                 // Insert, Delete
        0x5B or 0x5C or 0x5D or         // LWin, RWin, Apps
        0xA3 or 0xA5 or                 // RCtrl, RAlt
        0x6F;                           // Numpad Divide

    // --- interop ---
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
