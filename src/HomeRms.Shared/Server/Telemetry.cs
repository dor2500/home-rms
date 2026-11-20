using Grpc.Core;
using HomeRms.Shared.Protocol;

namespace HomeRms.Shared.Server;

/// <summary>A stateful sampler: CPU and network rates are deltas since the previous call.</summary>
public interface ITelemetrySampler
{
    /// <summary>Captures the baseline counters (called once before the first <see cref="Sample"/>).</summary>
    void Prime();

    TelemetrySample Sample();
}

public static class TelemetryStreamer
{
    public const int MinIntervalMs = 1000;
    public const int MaxIntervalMs = 10_000;

    public static int ClampInterval(int requested) =>
        requested <= 0 ? 2000 : Math.Clamp(requested, MinIntervalMs, MaxIntervalMs);

    /// <summary>Streams samples until the client cancels. The first sample follows a short baseline window.</summary>
    public static async Task RunAsync(ITelemetrySampler sampler, TelemetryRequest request,
        IServerStreamWriter<TelemetrySample> writer, ServerCallContext context, string op, Action<string> log)
    {
        var interval = ClampInterval(request.IntervalMs);
        log($"[telemetry] '{op}' started live telemetry ({interval} ms)");
        var sent = 0;
        try
        {
            sampler.Prime();
            await Task.Delay(Math.Min(interval, 500), context.CancellationToken);
            while (!context.CancellationToken.IsCancellationRequested)
            {
                var sample = sampler.Sample();
                sample.TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await writer.WriteAsync(sample, context.CancellationToken);
                sent++;
                await Task.Delay(interval, context.CancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            log($"[telemetry] '{op}' stopped live telemetry ({sent} samples)");
        }
    }
}
