namespace ModelSync.Core;

/// <summary>
/// An element of the domain-agnostic model: an identity, an optional type
/// reference (which is itself an element — streamlined MOF, so metamodels are
/// synchronized through the very same operations), and a bag of properties.
///
/// Elements are soft-deleted: a delete marks the element as gone from the
/// user-facing model while history replay can still apply property changes to
/// it, and a later create with the same id resurrects it.
/// </summary>
public sealed class ElementState
{
    private readonly Dictionary<string, PropertyState> _properties = new(StringComparer.Ordinal);

    public ElementState(string id, string? typeId)
    {
        Id = id;
        TypeId = typeId;
    }

    public string Id { get; }
    public string? TypeId { get; internal set; }
    public bool IsAlive { get; internal set; } = true;

    public IReadOnlyDictionary<string, PropertyState> Properties => _properties;

    public PropertyState GetOrCreateProperty(string name)
    {
        if (!_properties.TryGetValue(name, out var property))
        {
            property = new PropertyState(name);
            _properties[name] = property;
        }

        return property;
    }

    public PropertyState? GetProperty(string name) => _properties.GetValueOrDefault(name);
}
