using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

/// <summary>Operation factory helpers for concise test setup.</summary>
public static class Op
{
    public static Operation Create(string elementId, string? typeId = null, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.CreateElement,
        WorkspaceId = ws,
        ElementId = elementId,
        ElementTypeId = typeId
    };

    public static Operation Delete(string elementId, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.DeleteElement,
        WorkspaceId = ws,
        ElementId = elementId
    };

    public static Operation Set(string elementId, string property, string value, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.SetProperty,
        WorkspaceId = ws,
        ElementId = elementId,
        PropertyName = property,
        Value = PropertyValue.String(value)
    };

    public static Operation Unset(string elementId, string property, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.UnsetProperty,
        WorkspaceId = ws,
        ElementId = elementId,
        PropertyName = property
    };

    public static Operation AddSet(string elementId, string property, string value, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.AddSetItem,
        WorkspaceId = ws,
        ElementId = elementId,
        PropertyName = property,
        Value = PropertyValue.String(value)
    };

    public static Operation RemoveSet(string elementId, string property, string value, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.RemoveSetItem,
        WorkspaceId = ws,
        ElementId = elementId,
        PropertyName = property,
        Value = PropertyValue.String(value)
    };

    public static Operation Put(string elementId, string property, string key, string value, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.PutMapEntry,
        WorkspaceId = ws,
        ElementId = elementId,
        PropertyName = property,
        MapKey = key,
        Value = PropertyValue.String(value)
    };

    public static Operation RemoveMap(string elementId, string property, string key, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.RemoveMapEntry,
        WorkspaceId = ws,
        ElementId = elementId,
        PropertyName = property,
        MapKey = key
    };

    public static Operation Insert(string elementId, string property, string itemId, string value, string? after, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.InsertListItem,
        WorkspaceId = ws,
        ElementId = elementId,
        PropertyName = property,
        ItemId = itemId,
        AfterItemId = after,
        Value = PropertyValue.String(value)
    };

    public static Operation RemoveItem(string elementId, string property, string itemId, string ws = "") => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.RemoveListItem,
        WorkspaceId = ws,
        ElementId = elementId,
        PropertyName = property,
        ItemId = itemId
    };
}

public static class ModelAssert
{
    /// <summary>Asserts that the user-facing states of two models are identical.</summary>
    public static void Equivalent(ModelState expected, ModelState actual)
    {
        var expectedElements = expected.Elements.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        var actualElements = actual.Elements.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

        Assert.Equal(expectedElements.Select(e => e.Id), actualElements.Select(e => e.Id));

        foreach (var (e, a) in expectedElements.Zip(actualElements))
        {
            Assert.Equal(e.TypeId, a.TypeId);

            var expectedProps = e.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var actualProps = a.Properties.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.Equal(expectedProps, actualProps);

            foreach (var name in expectedProps)
            {
                var ep = e.Properties[name];
                var ap = a.Properties[name];
                Assert.Equal(ep.Cardinality, ap.Cardinality);
                Assert.Equal(ep.SingleValue, ap.SingleValue);
                Assert.Equal(
                    ep.SetValues.Select(v => v.MembershipKey).OrderBy(v => v, StringComparer.Ordinal),
                    ap.SetValues.Select(v => v.MembershipKey).OrderBy(v => v, StringComparer.Ordinal));
                Assert.Equal(
                    ep.MapValues.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.Content}"),
                    ap.MapValues.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.Content}"));
                Assert.Equal(
                    ep.ListItems.Select(i => $"{i.ItemId}={i.Value.Content}"),
                    ap.ListItems.Select(i => $"{i.ItemId}={i.Value.Content}"));
            }
        }
    }

    public static IReadOnlyList<string> ListValues(ModelState model, string elementId, string property) =>
        model.GetElementIncludingDeleted(elementId)?.GetProperty(property)?.ListItems.Select(i => i.Value.Content).ToList()
        ?? new List<string>();
}
