using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HomeRms.Shared.Protocol;

namespace HomeRms.Agent.Features;

/// <summary>Injects mouse and keyboard events into the local session via SendInput.</summary>
[SupportedOSPlatform("windows")]
public static class InputInjector
{
    public static void Apply(InputEvent e)
    {
        // Absolute coordinates are normalized 0..65535 across the PRIMARY monitor,
        // matching the primary-screen capture the agent streams.
        switch (e.Kind)
        {
            case InputEvent.Types.Kind.MouseMove:
                SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, e.X, e.Y, 0);
                break;
            case InputEvent.Types.Kind.MouseDown:
                SendMouse(DownFlag(e.Button) | MOUSEEVENTF_ABSOLUTE, e.X, e.Y, 0);
                break;
            case InputEvent.Types.Kind.MouseUp:
                SendMouse(UpFlag(e.Button) | MOUSEEVENTF_ABSOLUTE, e.X, e.Y, 0);
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
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = down ? 0u : KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

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
    private const uint KEYEVENTF_KEYUP = 0x0002;

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
