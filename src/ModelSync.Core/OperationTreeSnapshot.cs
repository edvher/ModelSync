namespace ModelSync.Core;

public record OperationTreeSnapshot(Guid RootId, IReadOnlyDictionary<Guid, OperationNodeSnapshot> Nodes);

public record OperationNodeSnapshot(Guid Id, Operation Operation, Guid? Parent, IReadOnlyList<Guid> Children);
