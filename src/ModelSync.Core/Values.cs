namespace ModelSync.Core;

/// <summary>The primitive kind of a property value.</summary>
public enum ValueKind
{
    String,
    Integer,
    Double,
    Boolean,
    /// <summary>Reference to another element (content holds the element id).</summary>
    Reference,
    Json
}

/// <summary>
/// An immutable, domain-agnostic property value. Equality is structural
/// (kind + content), which also serves as the membership key for sets.
/// </summary>
public sealed record PropertyValue(ValueKind Kind, string Content)
{
    public static PropertyValue String(string value) => new(ValueKind.String, value);
    public static PropertyValue Integer(long value) => new(ValueKind.Integer, value.ToString());
    public static PropertyValue Double(double value) => new(ValueKind.Double, value.ToString("R"));
    public static PropertyValue Boolean(bool value) => new(ValueKind.Boolean, value ? "true" : "false");
    public static PropertyValue Reference(string elementId) => new(ValueKind.Reference, elementId);

    /// <summary>Canonical key used for set membership and conflict keying.</summary>
    public string MembershipKey => $"{Kind}:{Content}";

    public override string ToString() => Content;
}
