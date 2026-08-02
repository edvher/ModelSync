using System.Collections.Concurrent;
using System.Threading.Channels;
using ModelSync.Core;

namespace ModelSync.Server.Services;

/// <summary>
/// Fan-out of applied operations to streaming subscribers, per workspace
/// branch. Wired to <see cref="ModelService.OperationsApplied"/>.
/// </summary>
public sealed class OperationHub
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<Operation>>> _subscribers = new(StringComparer.Ordinal);

    public OperationHub(ModelService service)
    {
        service.OperationsApplied += Publish;
    }

    public void Publish(string workspaceId, IReadOnlyList<Operation> operations)
    {
        if (!_subscribers.TryGetValue(workspaceId, out var channels))
        {
            return;
        }

        foreach (var channel in channels.Values)
        {
            foreach (var operation in operations)
            {
                channel.Writer.TryWrite(operation);
            }
        }
    }

    public IDisposable Subscribe(string workspaceId, out ChannelReader<Operation> reader)
    {
        var channels = _subscribers.GetOrAdd(workspaceId, _ => new ConcurrentDictionary<Guid, Channel<Operation>>());
        var subscriptionId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<Operation>();
        channels[subscriptionId] = channel;
        reader = channel.Reader;
        return new Subscription(() =>
        {
            channels.TryRemove(subscriptionId, out _);
            channel.Writer.TryComplete();
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
