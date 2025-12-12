namespace ModelSync.Core;

public record PropertyDefinition(
    string Name,
    PropertyKind Kind,
    CollectionKind Collection)
{
    public string? ReferenceType { get; init; }
}

public record ElementType(string Name)
{
    public Dictionary<string, PropertyDefinition> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddProperty(PropertyDefinition definition)
    {
        Properties[definition.Name] = definition;
    }
}
