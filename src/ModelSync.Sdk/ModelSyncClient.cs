using System.Runtime.CompilerServices;
using Grpc.Net.Client;
using ModelSync.Core;
using ModelSync.Protocol;
using CoreOperation = ModelSync.Core.Operation;

namespace ModelSync.Sdk;

/// <summary>Thrown when the server rejects an operation or a synchronization request.</summary>
public sealed class ModelSyncException(string message) : Exception(message);

/// <summary>
/// Low-level client for the ModelSync server. All methods speak in core model
/// types; the wire protocol stays an implementation detail.
/// </summary>
public sealed class ModelSyncClient : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly ModelSyncService.ModelSyncServiceClient _client;

    /// <param name="address">Server address, e.g. http://localhost:5001.</param>
    /// <param name="httpHandler">Optional handler (used by in-process tests).</param>
    public ModelSyncClient(string address, HttpMessageHandler? httpHandler = null)
    {
        _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = httpHandler });
        _client = new ModelSyncService.ModelSyncServiceClient(_channel);
    }

    public async Task<IReadOnlyList<CoreOperation>> CheckoutAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var response = await _client.CheckoutAsync(new CheckoutRequest { WorkspaceId = workspaceId }, cancellationToken: cancellationToken);
        return response.Operations.Select(ProtoMapper.ToOperation).ToList();
    }

    public async Task<CoreOperation> ApplyAsync(string workspaceId, CoreOperation operation, CancellationToken cancellationToken = default)
    {
        var response = await _client.ApplyAsync(new ApplyRequest
        {
            WorkspaceId = workspaceId,
            Operation = ProtoMapper.ToMessage(operation)
        }, cancellationToken: cancellationToken);

        if (!response.Accepted)
        {
            throw new ModelSyncException(response.Error);
        }

        return ProtoMapper.ToOperation(response.Operation);
    }

    public async Task<UpdateResult> UpdateAsync(
        string workspaceId,
        Core.ResolutionStrategy strategy = Core.ResolutionStrategy.ChildWins,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.UpdateAsync(new UpdateRequest
        {
            WorkspaceId = workspaceId,
            Strategy = (Protocol.ResolutionStrategy)(int)strategy
        }, cancellationToken: cancellationToken);

        return new UpdateResult(
            response.WasUpToDate,
            response.PublicOperations.Select(ProtoMapper.ToOperation).ToList(),
            response.Conflicts.Select(ProtoMapper.ToConflict).ToList(),
            response.ResolutionOperations.Select(ProtoMapper.ToOperation).ToList());
    }

    public async Task<CommitResult> CommitAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var response = await _client.CommitAsync(new CommitRequest { WorkspaceId = workspaceId }, cancellationToken: cancellationToken);
        return new CommitResult(
            response.Success,
            string.IsNullOrEmpty(response.Reason) ? null : response.Reason,
            response.CommittedOperations.Select(ProtoMapper.ToOperation).ToList());
    }

    public async Task<IReadOnlyList<Core.Conflict>> GetAwarenessConflictsAsync(
        string workspaceA,
        string workspaceB,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.GetAwarenessConflictsAsync(new AwarenessRequest
        {
            WorkspaceA = workspaceA,
            WorkspaceB = workspaceB
        }, cancellationToken: cancellationToken);

        return response.Conflicts.Select(ProtoMapper.ToConflict).ToList();
    }

    public async Task<IReadOnlyList<string>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.ListWorkspacesAsync(new ListWorkspacesRequest(), cancellationToken: cancellationToken);
        return response.WorkspaceIds.ToList();
    }

    public async IAsyncEnumerable<CoreOperation> SubscribeAsync(
        string workspaceId,
        bool skipReplay = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.Subscribe(new SubscribeRequest
        {
            WorkspaceId = workspaceId,
            SkipReplay = skipReplay
        }, cancellationToken: cancellationToken);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return ProtoMapper.ToOperation(call.ResponseStream.Current.Operation);
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }
}
