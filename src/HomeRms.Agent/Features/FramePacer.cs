using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomeRms.Agent.Features;

/// <summary>
/// Frame-rate cap for the screen stream, accurate at 60–120 fps.
/// <see cref="Task.Delay(int)"/> rounds up to the 15.6 ms system tick, which turns "60 fps" into ~32 fps;
/// this pacer keeps Stopwatch deadlines on a steady cadence and sleeps on a high-resolution waitable
/// timer (Windows 10 1803+), falling back to a short sleep + spin when that timer is unavailable.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FramePacer : IDisposable
{
    private readonly long _intervalTicks;
    private readonly IntPtr _timer;
    private long _next; // Stopwatch timestamp of the earliest moment the next frame may be sent

    public FramePacer(double fps)
    {
        _intervalTicks = (long)(Stopwatch.Frequency / Math.Clamp(fps, 0.1, 1000));
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        _next = Stopwatch.GetTimestamp();
    }

    public double IntervalMs => _intervalTicks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Waits until the next frame slot (returns at once when it has already started).</summary>
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        var remaining = TicksToMs(_next - Stopwatch.GetTimestamp());
        if (remaining <= 0.05) return;

        // Long waits (low fps): sleep coarsely and stay cancellable, then finish precisely
        if (remaining > 40)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remaining - 20), ct);
            remaining = TicksToMs(_next - Stopwatch.GetTimestamp());
            if (remaining <= 0.05) return;
        }
        ct.ThrowIfCancellationRequested();
        SleepPrecise(remaining);
    }

    /// <summary>
    /// Records that a frame captured at <paramref name="capturedAt"/> (Stopwatch timestamp) was sent.
    /// While frames keep coming the cadence stays fixed (no drift); after an idle gap it restarts from now.
    /// </summary>
    public void MarkSent(long capturedAt)
    {
        _next = capturedAt - _next < _intervalTicks ? _next + _intervalTicks : capturedAt + _intervalTicks;
    }

    private void SleepPrecise(double ms)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
        if (_timer != IntPtr.Zero)
        {
            // Negative due time = relative, in 100 ns units
            var due = -(long)(ms * 10_000);
            if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_timer, (uint)Math.Ceiling(ms) + 50);
                return;
            }
        }

        // Fallback: coarse sleep, then spin the last stretch
        while (true)
        {
            var left = TicksToMs(deadline - Stopwatch.GetTimestamp());
            if (left <= 0) return;
            if (left > 2) Thread.Sleep(1);
            else Thread.SpinWait(50);
        }
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
    }

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
