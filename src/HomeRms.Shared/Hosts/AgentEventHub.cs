using System.Threading.Channels;
using Grpc.Core;
using HomeRms.Shared.Protocol;

namespace HomeRms.Shared.Hosts;

/// <summary>
/// Fan-out of agent events to every active WatchAgentEvents stream. Each subscriber has its own bounded
/// channel that drops the oldest event when a slow consumer falls behind, so a stuck console can never
/// block the listener or grow memory.
/// </summary>
public sealed class AgentEventHub
{
    public const int SubscriberCapacity = 256;

    private readonly object _gate = new();
    private readonly List<Channel<AgentEvent>> _subscribers = new();

    public int SubscriberCount
    {
        get { lock (_gate) return _subscribers.Count; }
    }

    public void Publish(AgentEvent e)
    {
        Channel<AgentEvent>[] subs;
        lock (_gate) subs = _subscribers.ToArray();
        foreach (var s in subs) s.Writer.TryWrite(e);
    }

    public (ChannelReader<AgentEvent> Reader, IDisposable Subscription) Subscribe()
    {
        var ch = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_gate) _subscribers.Add(ch);
        return (ch.Reader, new Unsubscriber(this, ch));
    }

    /// <summary>Streams events to one gRPC client until it disconnects.</summary>
    public async Task StreamAsync(IServerStreamWriter<AgentEvent> writer, ServerCallContext context, string op, Action<string> log)
    {
        var (reader, sub) = Subscribe();
        log($"[events] '{op}' subscribed to agent events");
        var sent = 0;
        try
        {
            await foreach (var e in reader.ReadAllAsync(context.CancellationToken))
            {
                await writer.WriteAsync(e, context.CancellationToken);
                sent++;
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { /* stream closed by the client */ }
        finally
        {
            sub.Dispose();
            log($"[events] '{op}' unsubscribed ({sent} events delivered)");
        }
    }

    private sealed class Unsubscriber(AgentEventHub hub, Channel<AgentEvent> ch) : IDisposable
    {
        public void Dispose()
        {
            lock (hub._gate) hub._subscribers.Remove(ch);
            ch.Writer.TryComplete();
        }
    }
}
