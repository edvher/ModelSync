namespace ModelSync.Core;

public record PropertyValue(ValueKind Kind, string? Content)
{
    public static PropertyValue FromString(string value) => new(ValueKind.String, value);
    public static PropertyValue FromReference(string value) => new(ValueKind.Reference, value);
}

public record ListEntry(string ItemId, PropertyValue Value);

public class PropertyContainer
{
    public PropertyDefinition Definition { get; }

    private PropertyValue? _scalarValue;
    private readonly List<ListEntry> _list = new();
    private readonly HashSet<PropertyValue> _set = new();
    private readonly Dictionary<string, PropertyValue> _map = new(StringComparer.OrdinalIgnoreCase);

    public PropertyContainer(PropertyDefinition definition)
    {
        Definition = definition;
    }

    public PropertyValue? ScalarValue => _scalarValue;
    public IReadOnlyList<ListEntry> ListValues => _list;
    public IReadOnlyCollection<PropertyValue> SetValues => _set;
    public IReadOnlyDictionary<string, PropertyValue> MapValues => _map;

    public void Set(PropertyValue value)
    {
        _scalarValue = value;
    }

    public string AddToList(PropertyValue value, string? afterItemId)
    {
        var entry = new ListEntry(Guid.NewGuid().ToString("N"), value);
        if (afterItemId is null)
        {
            _list.Insert(0, entry);
        }
        else
        {
            var index = _list.FindIndex(i => i.ItemId == afterItemId);
            _list.Insert(index >= 0 ? index + 1 : _list.Count, entry);
        }

        return entry.ItemId;
    }

    public void RemoveFromList(string itemId)
    {
        var index = _list.FindIndex(i => i.ItemId == itemId);
        if (index >= 0)
        {
            _list.RemoveAt(index);
        }
    }

    public void AddToSet(PropertyValue value)
    {
        _set.Add(value);
    }

    public void RemoveFromSet(PropertyValue value)
    {
        _set.Remove(value);
    }

    public void UpdateMap(string key, PropertyValue value)
    {
        _map[key] = value;
    }

    public void RemoveMapEntry(string key)
    {
        _map.Remove(key);
    }

    public string? GetLastListItemId() => _list.LastOrDefault()?.ItemId;
}

public class ModelElement
{
    public string Id { get; }
    public string ElementType { get; }
    public Dictionary<string, PropertyContainer> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ModelElement(string id, string elementType)
    {
        Id = id;
        ElementType = elementType;
    }

    public void AddProperty(PropertyDefinition definition)
    {
        Properties[definition.Name] = new PropertyContainer(definition);
    }
}
