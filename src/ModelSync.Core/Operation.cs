namespace ModelSync.Core;

public class Operation
{
    public Operation(Guid id, OperationType type)
    {
        Id = id;
        Type = type;
    }

    public Guid Id { get; init; }
    public OperationType Type { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public string ElementId { get; init; } = string.Empty;
    public string? ElementType { get; init; }
    public string? PropertyName { get; init; }
    public PropertyValue? NewValue { get; init; }
    public string? AfterItemId { get; init; }
    public string? ItemId { get; init; }
    public string? MapKey { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public record OperationNode(Operation Operation, Guid? Parent)
{
    public List<Guid> Children { get; } = new();
}
