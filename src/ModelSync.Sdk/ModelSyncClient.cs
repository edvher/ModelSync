using System.Runtime.CompilerServices;
using Grpc.Net.Client;
using ModelSync.Server;

namespace ModelSync.Sdk;

public class ModelSyncClient : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly ModelSyncApi.ModelSyncApiClient _client;

    public ModelSyncClient(string address)
    {
        _channel = GrpcChannel.ForAddress(address);
        _client = new ModelSyncApi.ModelSyncApiClient(_channel);
    }

    public async Task<IReadOnlyList<OperationMessage>> CheckoutAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var response = await _client.CheckoutAsync(new CheckoutRequest { ModelName = modelName }, cancellationToken: cancellationToken);
        return response.Operations.ToList();
    }

    public async Task<string> ApplyAsync(string modelName, OperationMessage operation, CancellationToken cancellationToken = default)
    {
        var response = await _client.ApplyOperationAsync(new ApplyOperationRequest
        {
            ModelName = modelName,
            Operation = operation
        }, cancellationToken: cancellationToken);

        return response.OperationId;
    }

    public async IAsyncEnumerable<OperationMessage> SubscribeAsync(string modelName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.SubscribeOperations(new SubscribeRequest { ModelName = modelName }, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (call.ResponseStream.Current?.Operation is { } op)
            {
                yield return op;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }

}
