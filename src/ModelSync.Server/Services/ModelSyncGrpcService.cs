using Grpc.Core;
using ModelSync.Core;
using ModelSync.Protocol;

namespace ModelSync.Server.Services;

/// <summary>The gRPC facade over the core <see cref="ModelService"/>.</summary>
public sealed class ModelSyncGrpcService : ModelSyncService.ModelSyncServiceBase
{
    private readonly ModelService _service;
    private readonly ConflictAwarenessService _awareness;
    private readonly OperationHub _hub;

    public ModelSyncGrpcService(ModelService service, ConflictAwarenessService awareness, OperationHub hub)
    {
        _service = service;
        _awareness = awareness;
        _hub = hub;
    }

    public override Task<CheckoutResponse> Checkout(CheckoutRequest request, ServerCallContext context)
    {
        _service.Checkout(request.WorkspaceId);
        var response = new CheckoutResponse();
        response.Operations.AddRange(_service.History(request.WorkspaceId).Select(ProtoMapper.ToMessage));
        return Task.FromResult(response);
    }

    public override Task<ApplyResponse> Apply(ApplyRequest request, ServerCallContext context)
    {
        try
        {
            var operation = ProtoMapper.ToOperation(request.Operation) with { WorkspaceId = request.WorkspaceId };
            var applied = _service.Apply(request.WorkspaceId, operation);
            return Task.FromResult(new ApplyResponse
            {
                Accepted = true,
                Operation = ProtoMapper.ToMessage(applied)
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Task.FromResult(new ApplyResponse { Accepted = false, Error = ex.Message });
        }
    }

    public override Task<UpdateResponse> Update(UpdateRequest request, ServerCallContext context)
    {
        try
        {
            var result = _service.Update(request.WorkspaceId, (Core.ResolutionStrategy)(int)request.Strategy);
            var response = new UpdateResponse { WasUpToDate = result.WasUpToDate };
            response.PublicOperations.AddRange(result.PublicOperations.Select(ProtoMapper.ToMessage));
            response.Conflicts.AddRange(result.Conflicts.Select(ProtoMapper.ToMessage));
            response.ResolutionOperations.AddRange(result.ResolutionOperations.Select(ProtoMapper.ToMessage));
            return Task.FromResult(response);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override Task<CommitResponse> Commit(CommitRequest request, ServerCallContext context)
    {
        try
        {
            var result = _service.Commit(request.WorkspaceId);
            var response = new CommitResponse
            {
                Success = result.Success,
                Reason = result.Reason ?? string.Empty
            };
            response.CommittedOperations.AddRange(result.CommittedOperations.Select(ProtoMapper.ToMessage));
            return Task.FromResult(response);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<OperationEvent> responseStream, ServerCallContext context)
    {
        _service.Checkout(request.WorkspaceId);

        // Register the live subscription before snapshotting the history so no
        // operation can fall between replay and streaming; replayed operation
        // ids are skipped when they arrive again through the channel.
        using var subscription = _hub.Subscribe(request.WorkspaceId, out var reader);

        var replayed = new HashSet<Guid>();
        if (!request.SkipReplay)
        {
            foreach (var operation in _service.History(request.WorkspaceId))
            {
                replayed.Add(operation.Id);
                await responseStream.WriteAsync(new OperationEvent
                {
                    WorkspaceId = request.WorkspaceId,
                    Operation = ProtoMapper.ToMessage(operation)
                });
            }
        }

        try
        {
            await foreach (var operation in reader.ReadAllAsync(context.CancellationToken))
            {
                if (replayed.Count > 0 && replayed.Remove(operation.Id))
                {
                    continue;
                }

                await responseStream.WriteAsync(new OperationEvent
                {
                    WorkspaceId = request.WorkspaceId,
                    Operation = ProtoMapper.ToMessage(operation)
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away — normal end of a subscription.
        }
    }

    public override Task<AwarenessResponse> GetAwarenessConflicts(AwarenessRequest request, ServerCallContext context)
    {
        var response = new AwarenessResponse();
        response.Conflicts.AddRange(
            _awareness.GetConflicts(request.WorkspaceA, request.WorkspaceB).Select(ProtoMapper.ToMessage));
        return Task.FromResult(response);
    }

    public override Task<ListWorkspacesResponse> ListWorkspaces(ListWorkspacesRequest request, ServerCallContext context)
    {
        var response = new ListWorkspacesResponse();
        response.WorkspaceIds.AddRange(_service.Workspaces.OrderBy(id => id, StringComparer.Ordinal));
        return Task.FromResult(response);
    }
}
