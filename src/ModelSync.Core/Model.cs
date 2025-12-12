namespace ModelSync.Core;

public class Model
{
    private readonly OperationTree _tree;

    public Model(string name, OperationTree tree)
    {
        Name = name;
        _tree = tree;
    }

    public string Name { get; }
    public Dictionary<string, ModelElement> Elements { get; } = new();
    public Dictionary<string, ElementType> ElementTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ModelElement? GetElement(string id) => Elements.TryGetValue(id, out var element) ? element : null;

    public void AddElementType(ElementType elementType) => ElementTypes[elementType.Name] = elementType;

    public void ExecuteAll(IEnumerable<Operation> operations, bool history)
    {
        foreach (var op in operations)
        {
            Execute(op, history);
        }
    }

    public void Execute(Operation op, bool history)
    {
        if (!history)
        {
            _tree.AddOperationToModel(Name, op);
            _tree.SetToken(Name, op);
        }

        switch (op.Type)
        {
            case OperationType.CreateElement:
                CreateElement(op.ElementId, op.ElementType ?? string.Empty);
                break;
            case OperationType.DeleteElement:
                DeleteElement(op.ElementId);
                break;
            case OperationType.SetProperty:
                SetProperty(op.ElementId, op.PropertyName ?? string.Empty, op.NewValue!);
                break;
            case OperationType.AddToListProperty:
                AddToList(op.ElementId, op.PropertyName ?? string.Empty, op.NewValue!, op.AfterItemId);
                break;
            case OperationType.RemoveFromListProperty:
                RemoveFromList(op.ElementId, op.PropertyName ?? string.Empty, op.ItemId ?? string.Empty);
                break;
            case OperationType.AddToSetProperty:
                AddToSet(op.ElementId, op.PropertyName ?? string.Empty, op.NewValue!);
                break;
            case OperationType.RemoveFromSetProperty:
                RemoveFromSet(op.ElementId, op.PropertyName ?? string.Empty, op.NewValue!);
                break;
            case OperationType.UpdateMapEntry:
                UpdateMapEntry(op.ElementId, op.PropertyName ?? string.Empty, op.MapKey ?? string.Empty, op.NewValue!);
                break;
            case OperationType.RemoveMapEntry:
                RemoveMapEntry(op.ElementId, op.PropertyName ?? string.Empty, op.MapKey ?? string.Empty);
                break;
        }
    }

    private void CreateElement(string id, string elementType)
    {
        var element = new ModelElement(id, elementType);
        if (ElementTypes.TryGetValue(elementType, out var type))
        {
            foreach (var prop in type.Properties.Values)
            {
                element.AddProperty(prop);
            }
        }

        Elements[id] = element;
    }

    private void DeleteElement(string id)
    {
        Elements.Remove(id);
    }

    private void SetProperty(string elementId, string propertyName, PropertyValue newValue)
    {
        var container = GetOrCreateContainer(elementId, propertyName, CollectionKind.Scalar);
        container.Set(newValue);
    }

    private void AddToList(string elementId, string propertyName, PropertyValue value, string? afterItemId)
    {
        var container = GetOrCreateContainer(elementId, propertyName, CollectionKind.List);
        container.AddToList(value, afterItemId);
    }

    private void RemoveFromList(string elementId, string propertyName, string itemId)
    {
        var container = GetOrCreateContainer(elementId, propertyName, CollectionKind.List);
        container.RemoveFromList(itemId);
    }

    private void AddToSet(string elementId, string propertyName, PropertyValue value)
    {
        var container = GetOrCreateContainer(elementId, propertyName, CollectionKind.Set);
        container.AddToSet(value);
    }

    private void RemoveFromSet(string elementId, string propertyName, PropertyValue value)
    {
        var container = GetOrCreateContainer(elementId, propertyName, CollectionKind.Set);
        container.RemoveFromSet(value);
    }

    private void UpdateMapEntry(string elementId, string propertyName, string mapKey, PropertyValue value)
    {
        var container = GetOrCreateContainer(elementId, propertyName, CollectionKind.Map);
        container.UpdateMap(mapKey, value);
    }

    private void RemoveMapEntry(string elementId, string propertyName, string mapKey)
    {
        var container = GetOrCreateContainer(elementId, propertyName, CollectionKind.Map);
        container.RemoveMapEntry(mapKey);
    }

    private PropertyContainer GetOrCreateContainer(string elementId, string propertyName, CollectionKind collection)
    {
        if (!Elements.TryGetValue(elementId, out var element))
        {
            element = new ModelElement(elementId, string.Empty);
            Elements[elementId] = element;
        }

        if (!element.Properties.TryGetValue(propertyName, out var container))
        {
            var definition = new PropertyDefinition(propertyName, PropertyKind.Primitive, collection);
            container = new PropertyContainer(definition);
            element.AddProperty(definition);
        }

        return container;
    }
}
