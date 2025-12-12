using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ModelSync.Core;

namespace ModelSync.Server.Services;

public class OperationHub
{
    private readonly ConcurrentDictionary<string, Channel<Operation>> _channels = new(StringComparer.OrdinalIgnoreCase);

    public IAsyncEnumerable<Operation> Subscribe(string modelName, CancellationToken cancellationToken)
    {
        var channel = _channels.GetOrAdd(modelName, _ => Channel.CreateUnbounded<Operation>());
        return ReadAsync(channel.Reader, cancellationToken);
    }

    public void Publish(string modelName, Operation operation)
    {
        var channel = _channels.GetOrAdd(modelName, _ => Channel.CreateUnbounded<Operation>());
        channel.Writer.TryWrite(operation);
    }

    private static async IAsyncEnumerable<Operation> ReadAsync(ChannelReader<Operation> reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var operation))
            {
                yield return operation;
            }
        }
    }
}
