using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

public class ModelStateTests
{
    [Fact]
    public void CreateSetAndReadBack()
    {
        var model = new ModelState("test");
        model.Apply(Op.Create("e1", "Class"));
        model.Apply(Op.Set("e1", "name", "Monitor"));

        var element = Assert.Single(model.Elements);
        Assert.Equal("e1", element.Id);
        Assert.Equal("Class", element.TypeId);
        Assert.Equal("Monitor", element.GetProperty("name")!.SingleValue!.Content);
    }

    [Fact]
    public void DeleteIsSoftAndCreateResurrectsWithProperties()
    {
        var model = new ModelState("test");
        model.Apply(Op.Create("e1", "Class"));
        model.Apply(Op.Set("e1", "name", "Monitor"));
        model.Apply(Op.Delete("e1"));

        Assert.Null(model.GetElement("e1"));
        Assert.NotNull(model.GetElementIncludingDeleted("e1"));

        // Operations from history still apply to deleted elements.
        model.Apply(Op.Set("e1", "name", "Sensor"));
        Assert.Null(model.GetElement("e1"));

        // Create with the same id resurrects the element with its properties.
        model.Apply(Op.Create("e1"));
        var element = model.GetElement("e1");
        Assert.NotNull(element);
        Assert.Equal("Class", element!.TypeId);
        Assert.Equal("Sensor", element.GetProperty("name")!.SingleValue!.Content);
    }

    [Fact]
    public void SetAndMapSemantics()
    {
        var model = new ModelState("test");
        model.Apply(Op.Create("e1"));
        model.Apply(Op.AddSet("e1", "tags", "a"));
        model.Apply(Op.AddSet("e1", "tags", "a")); // idempotent
        model.Apply(Op.AddSet("e1", "tags", "b"));
        model.Apply(Op.RemoveSet("e1", "tags", "a"));

        var tags = model.GetElement("e1")!.GetProperty("tags")!;
        Assert.Equal(new[] { "b" }, tags.SetValues.Select(v => v.Content));

        model.Apply(Op.Put("e1", "attrs", "k1", "v1"));
        model.Apply(Op.Put("e1", "attrs", "k1", "v2"));
        model.Apply(Op.Put("e1", "attrs", "k2", "x"));
        model.Apply(Op.RemoveMap("e1", "attrs", "k2"));

        var attrs = model.GetElement("e1")!.GetProperty("attrs")!;
        Assert.Equal("v2", attrs.MapValues["k1"].Content);
        Assert.False(attrs.MapValues.ContainsKey("k2"));
    }

    [Fact]
    public void ListInsertHeadAnchorAndTailFallback()
    {
        var model = new ModelState("test");
        model.Apply(Op.Create("e1"));
        model.Apply(Op.Insert("e1", "items", "i-a", "A", after: null));
        model.Apply(Op.Insert("e1", "items", "i-b", "B", after: "i-a"));
        model.Apply(Op.Insert("e1", "items", "i-c", "C", after: "i-b"));
        model.Apply(Op.Insert("e1", "items", "i-h", "H", after: null)); // head insert
        model.Apply(Op.Insert("e1", "items", "i-x", "X", after: "missing")); // unknown anchor -> tail

        Assert.Equal(new[] { "H", "A", "B", "C", "X" }, ModelAssert.ListValues(model, "e1", "items"));
    }

    [Fact]
    public void ListRemoveTombstonesAndAnchorsOnTombstoneStillWork()
    {
        var model = new ModelState("test");
        model.Apply(Op.Create("e1"));
        model.Apply(Op.Insert("e1", "items", "i-a", "A", null));
        model.Apply(Op.Insert("e1", "items", "i-b", "B", "i-a"));
        model.Apply(Op.Insert("e1", "items", "i-c", "C", "i-b"));
        model.Apply(Op.RemoveItem("e1", "items", "i-b"));

        Assert.Equal(new[] { "A", "C" }, ModelAssert.ListValues(model, "e1", "items"));

        // Inserting after the tombstoned item keeps the intended position.
        model.Apply(Op.Insert("e1", "items", "i-y", "Y", "i-b"));
        Assert.Equal(new[] { "A", "Y", "C" }, ModelAssert.ListValues(model, "e1", "items"));
    }

    [Fact]
    public void ReExecutingAnInsertMovesTheItem()
    {
        var model = new ModelState("test");
        model.Apply(Op.Create("e1"));
        model.Apply(Op.Insert("e1", "items", "i-a", "A", null));
        model.Apply(Op.Insert("e1", "items", "i-b", "B", "i-a"));
        model.Apply(Op.Insert("e1", "items", "i-c", "C", "i-b"));

        // Re-executing B's insert (same item id) after C moves it.
        model.Apply(Op.Insert("e1", "items", "i-b", "B", "i-c"));
        Assert.Equal(new[] { "A", "C", "B" }, ModelAssert.ListValues(model, "e1", "items"));
    }

    [Fact]
    public void RemovedItemStaysRemovedWhenRelinked()
    {
        // "Delete always wins" for list items: moving a removed item does not resurrect it.
        var model = new ModelState("test");
        model.Apply(Op.Create("e1"));
        model.Apply(Op.Insert("e1", "items", "i-a", "A", null));
        model.Apply(Op.Insert("e1", "items", "i-b", "B", "i-a"));
        model.Apply(Op.RemoveItem("e1", "items", "i-b"));
        model.Apply(Op.Insert("e1", "items", "i-b", "B", null));

        Assert.Equal(new[] { "A" }, ModelAssert.ListValues(model, "e1", "items"));
    }

    [Fact]
    public void FirstAlivePredecessorSkipsTombstones()
    {
        var model = new ModelState("test");
        model.Apply(Op.Create("e1"));
        model.Apply(Op.Insert("e1", "items", "i-a", "A", null));
        model.Apply(Op.Insert("e1", "items", "i-b", "B", "i-a"));
        model.Apply(Op.Insert("e1", "items", "i-c", "C", "i-b"));
        model.Apply(Op.RemoveItem("e1", "items", "i-b"));

        var property = model.GetElement("e1")!.GetProperty("items")!;
        Assert.Equal("i-a", property.FirstAlivePredecessor("i-c"));
        Assert.Equal("i-a", property.FirstAlivePredecessor("i-b"));
        Assert.Null(property.FirstAlivePredecessor("i-a"));
        Assert.Equal("i-c", property.LastAliveItemId());
    }

    [Fact]
    public void SameOperationSequenceProducesSameState()
    {
        var ops = new[]
        {
            Op.Create("e1", "Class"),
            Op.Set("e1", "name", "Monitor"),
            Op.Insert("e1", "methods", "m1", "scan", null),
            Op.Insert("e1", "methods", "m2", "measure", "m1"),
            Op.AddSet("e1", "tags", "core"),
            Op.Put("e1", "notes", "todo", "review"),
            Op.RemoveItem("e1", "methods", "m1")
        };

        var a = new ModelState("a");
        var b = new ModelState("b");
        a.ApplyAll(ops);
        b.ApplyAll(ops);

        ModelAssert.Equivalent(a, b);
    }
}
