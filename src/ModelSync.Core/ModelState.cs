namespace ModelSync.Core;

/// <summary>
/// The materialized state of one workspace's model, produced exclusively by
/// applying operations in order. Applying the same operation sequence always
/// yields the same state, which is the foundation of the synchronization
/// approach: a model is the composition of the operations on its branch.
///
/// Application is total: operations from history never fail, even when they
/// target elements that were deleted in the meantime (soft-delete semantics).
/// Validation of *new* user operations happens in <see cref="ModelService"/>.
/// </summary>
public sealed class ModelState
{
    private readonly Dictionary<string, ElementState> _elements = new(StringComparer.Ordinal);

    public ModelState(string workspaceId)
    {
        WorkspaceId = workspaceId;
    }

    public string WorkspaceId { get; }

    /// <summary>All elements including soft-deleted ones.</summary>
    public IReadOnlyDictionary<string, ElementState> AllElements => _elements;

    /// <summary>The user-facing model: alive elements only.</summary>
    public IEnumerable<ElementState> Elements => _elements.Values.Where(e => e.IsAlive);

    public ElementState? GetElement(string id)
    {
        var element = _elements.GetValueOrDefault(id);
        return element is { IsAlive: true } ? element : null;
    }

    public ElementState? GetElementIncludingDeleted(string id) => _elements.GetValueOrDefault(id);

    public void ApplyAll(IEnumerable<Operation> operations)
    {
        foreach (var operation in operations)
        {
            Apply(operation);
        }
    }

    public void Apply(Operation operation)
    {
        switch (operation.Type)
        {
            case OperationType.Root:
                break;

            case OperationType.CreateElement:
            {
                if (_elements.TryGetValue(operation.ElementId, out var existing))
                {
                    // Create-after-delete resurrects the element and keeps its properties.
                    existing.IsAlive = true;
                    if (operation.ElementTypeId is not null)
                    {
                        existing.TypeId = operation.ElementTypeId;
                    }
                }
                else
                {
                    _elements[operation.ElementId] = new ElementState(operation.ElementId, operation.ElementTypeId);
                }

                break;
            }

            case OperationType.DeleteElement:
            {
                if (_elements.TryGetValue(operation.ElementId, out var element))
                {
                    element.IsAlive = false;
                }
                else
                {
                    _elements[operation.ElementId] = new ElementState(operation.ElementId, null) { IsAlive = false };
                }

                break;
            }

            default:
            {
                var element = GetOrCreateForHistory(operation.ElementId);
                var property = element.GetOrCreateProperty(RequireProperty(operation));
                ApplyPropertyOperation(property, operation);
                break;
            }
        }
    }

    private static void ApplyPropertyOperation(PropertyState property, Operation operation)
    {
        switch (operation.Type)
        {
            case OperationType.SetProperty:
                property.Set(RequireValue(operation));
                break;
            case OperationType.UnsetProperty:
                property.Unset();
                break;
            case OperationType.AddSetItem:
                property.AddSetItem(RequireValue(operation));
                break;
            case OperationType.RemoveSetItem:
                property.RemoveSetItem(RequireValue(operation));
                break;
            case OperationType.PutMapEntry:
                property.PutMapEntry(RequireMapKey(operation), RequireValue(operation));
                break;
            case OperationType.RemoveMapEntry:
                property.RemoveMapEntry(RequireMapKey(operation));
                break;
            case OperationType.InsertListItem:
                property.InsertListItem(RequireItemId(operation), RequireValue(operation), operation.AfterItemId);
                break;
            case OperationType.RemoveListItem:
                property.RemoveListItem(RequireItemId(operation));
                break;
            default:
                throw new InvalidOperationException($"Unsupported operation type {operation.Type}.");
        }
    }

    private ElementState GetOrCreateForHistory(string elementId)
    {
        if (!_elements.TryGetValue(elementId, out var element))
        {
            element = new ElementState(elementId, null);
            _elements[elementId] = element;
        }

        return element;
    }

    private static string RequireProperty(Operation operation) =>
        operation.PropertyName ?? throw new InvalidOperationException($"{operation.Type} requires a property name.");

    private static PropertyValue RequireValue(Operation operation) =>
        operation.Value ?? throw new InvalidOperationException($"{operation.Type} requires a value.");

    private static string RequireMapKey(Operation operation) =>
        operation.MapKey ?? throw new InvalidOperationException($"{operation.Type} requires a map key.");

    private static string RequireItemId(Operation operation) =>
        operation.ItemId ?? throw new InvalidOperationException($"{operation.Type} requires an item id.");
}
