using ModelSync.Core;
using CoreOperation = ModelSync.Core.Operation;

namespace ModelSync.Sdk;

/// <summary>
/// A connected client working in a private workspace, "Google Docs for
/// models": it keeps a local replica of the workspace's model (reconstructed
/// purely by replaying the operation history), offers a typed CRUD API whose
/// every edit becomes one incremental operation on the server, and
/// synchronizes with the public workspace via update (pull + conflict
/// resolution) and commit (push).
/// </summary>
public sealed class WorkspaceSession : IAsyncDisposable
{
    private readonly ModelSyncClient _client;
    private readonly bool _ownsClient;

    private WorkspaceSession(ModelSyncClient client, bool ownsClient, string workspaceId)
    {
        _client = client;
        _ownsClient = ownsClient;
        WorkspaceId = workspaceId;
        Model = new ModelState(workspaceId);
    }

    public string WorkspaceId { get; }

    /// <summary>The local replica of the workspace model.</summary>
    public ModelState Model { get; }

    public static async Task<WorkspaceSession> ConnectAsync(
        string address,
        string workspaceId,
        HttpMessageHandler? httpHandler = null,
        CancellationToken cancellationToken = default)
    {
        var session = new WorkspaceSession(new ModelSyncClient(address, httpHandler), ownsClient: true, workspaceId);
        await session.ReplayCheckoutAsync(cancellationToken);
        return session;
    }

    public static async Task<WorkspaceSession> ConnectAsync(
        ModelSyncClient client,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var session = new WorkspaceSession(client, ownsClient: false, workspaceId);
        await session.ReplayCheckoutAsync(cancellationToken);
        return session;
    }

    private async Task ReplayCheckoutAsync(CancellationToken cancellationToken)
    {
        var history = await _client.CheckoutAsync(WorkspaceId, cancellationToken);
        Model.ApplyAll(history);
    }

    // ------------------------------------------------------------------ edits

    public async Task<string> CreateElementAsync(string? elementId = null, string? typeId = null, CancellationToken ct = default)
    {
        var id = elementId ?? Guid.NewGuid().ToString("N");
        await SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.CreateElement,
            WorkspaceId = WorkspaceId,
            ElementId = id,
            ElementTypeId = typeId
        }, ct);
        return id;
    }

    public Task DeleteElementAsync(string elementId, CancellationToken ct = default) =>
        SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.DeleteElement,
            WorkspaceId = WorkspaceId,
            ElementId = elementId
        }, ct);

    public Task SetPropertyAsync(string elementId, string property, PropertyValue value, CancellationToken ct = default) =>
        SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.SetProperty,
            WorkspaceId = WorkspaceId,
            ElementId = elementId,
            PropertyName = property,
            Value = value
        }, ct);

    public Task UnsetPropertyAsync(string elementId, string property, CancellationToken ct = default) =>
        SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.UnsetProperty,
            WorkspaceId = WorkspaceId,
            ElementId = elementId,
            PropertyName = property
        }, ct);

    public Task AddSetItemAsync(string elementId, string property, PropertyValue value, CancellationToken ct = default) =>
        SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.AddSetItem,
            WorkspaceId = WorkspaceId,
            ElementId = elementId,
            PropertyName = property,
            Value = value
        }, ct);

    public Task RemoveSetItemAsync(string elementId, string property, PropertyValue value, CancellationToken ct = default) =>
        SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.RemoveSetItem,
            WorkspaceId = WorkspaceId,
            ElementId = elementId,
            PropertyName = property,
            Value = value
        }, ct);

    public Task PutMapEntryAsync(string elementId, string property, string key, PropertyValue value, CancellationToken ct = default) =>
        SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.PutMapEntry,
            WorkspaceId = WorkspaceId,
            ElementId = elementId,
            PropertyName = property,
            MapKey = key,
            Value = value
        }, ct);

    public Task RemoveMapEntryAsync(string elementId, string property, string key, CancellationToken ct = default) =>
        SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.RemoveMapEntry,
            WorkspaceId = WorkspaceId,
            ElementId = elementId,
            PropertyName = property,
            MapKey = key
        }, ct);

    /// <summary>Inserts a new list item after the given anchor (null = at the head).</summary>
    public async Task<string> InsertListItemAsync(
        string elementId,
        string property,
        PropertyValue value,
        string? afterItemId,
        CancellationToken ct = default)
    {
        var itemId = Guid.NewGuid().ToString("N");
        await SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.InsertListItem,
            WorkspaceId = WorkspaceId,
            ElementId = elementId,
            PropertyName = property,
            ItemId = itemId,
            AfterItemId = afterItemId,
            Value = value
        }, ct);
        return itemId;
    }

    /// <summary>Appends a new list item at the end of the list.</summary>
    public Task<string> AppendListItemAsync(string elementId, string property, PropertyValue value, CancellationToken ct = default)
    {
        var anchor = Model.GetElement(elementId)?.GetProperty(property)?.LastAliveItemId();
        return InsertListItemAsync(elementId, property, value, anchor, ct);
    }

    public Task RemoveListItemAsync(string elementId, string property, string itemId, CancellationToken ct = default) =>
        SendAsync(new CoreOperation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.RemoveListItem,
            WorkspaceId = WorkspaceId,
            ElementId = elementId,
            PropertyName = property,
            ItemId = itemId
        }, ct);

    // ---------------------------------------------------------------- syncing

    /// <summary>
    /// Pulls the public changes into this workspace. The server detects and
    /// resolves conflicts; the returned public and resolution operations are
    /// replayed into the local replica.
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(
        ResolutionStrategy strategy = ResolutionStrategy.ChildWins,
        CancellationToken ct = default)
    {
        var result = await _client.UpdateAsync(WorkspaceId, strategy, ct);
        Model.ApplyAll(result.PublicOperations);
        Model.ApplyAll(result.ResolutionOperations);
        return result;
    }

    /// <summary>Pushes this workspace's changes to the public workspace.</summary>
    public Task<CommitResult> CommitAsync(CancellationToken ct = default) => _client.CommitAsync(WorkspaceId, ct);

    /// <summary>Conflicts brewing between this workspace and another one (awareness).</summary>
    public Task<IReadOnlyList<Conflict>> GetConflictsWithAsync(string otherWorkspaceId, CancellationToken ct = default) =>
        _client.GetAwarenessConflictsAsync(otherWorkspaceId, WorkspaceId, ct);

    /// <summary>Live stream of operations committed to the public workspace.</summary>
    public IAsyncEnumerable<CoreOperation> SubscribePublicAsync(CancellationToken ct = default) =>
        _client.SubscribeAsync(ModelService.PublicWorkspaceId, skipReplay: true, ct);

    private async Task SendAsync(CoreOperation operation, CancellationToken ct)
    {
        var applied = await _client.ApplyAsync(WorkspaceId, operation, ct);
        Model.Apply(applied);
    }

    public ValueTask DisposeAsync() => _ownsClient ? _client.DisposeAsync() : ValueTask.CompletedTask;
}
