using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

/// <summary>
/// Bounded state-space exploration in the spirit of the thesis evaluation:
/// enumerate all combinations of concurrent atomic edits on both sides of the
/// star topology, run the full synchronization protocol for both resolution
/// strategies, and assert the golden invariants:
///  1. convergence — public, both privates and a fresh replay are identical;
///  2. deterministic winner semantics for real conflicts;
///  3. no operation is silently lost unless a deterministic rule says so
///     (e.g. "delete wins" for list items).
/// </summary>
public class StateSpaceOracleTests
{
    private const string A = "A";
    private const string B = "B";

    private sealed record Action(string Name, Func<ModelService, string, Operation?> Create);

    private static ModelService NewService(params Operation[] baseOps)
    {
        var service = new ModelService();
        service.Checkout(A);
        foreach (var op in baseOps)
        {
            service.Apply(A, op);
        }

        Assert.True(service.Commit(A).Success);
        service.Checkout(B);
        service.Update(B);
        return service;
    }

    private static void Dance(ModelService service, ResolutionStrategy strategy)
    {
        Assert.True(service.Commit(A).Success);
        service.Update(B, strategy);
        Assert.True(service.Commit(B).Success);
        service.Update(A, strategy);
    }

    private static void AssertConverged(ModelService service, string scenario)
    {
        var publicModel = service.GetModel(ModelService.PublicWorkspaceId);
        try
        {
            ModelAssert.Equivalent(publicModel, service.GetModel(A));
            ModelAssert.Equivalent(publicModel, service.GetModel(B));
            var fresh = service.Checkout("fresh-" + Guid.NewGuid().ToString("N"));
            ModelAssert.Equivalent(publicModel, fresh);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"Divergence in scenario [{scenario}]: {ex.Message}");
        }
    }

    // -------------------------------------------------- single-value property

    public static TheoryData<string, string, string> SingleValueCombinations()
    {
        var actions = new[] { "none", "set-own", "set-shared", "unset", "delete" };
        var data = new TheoryData<string, string, string>();
        foreach (var parent in actions)
        {
            foreach (var child in actions)
            {
                data.Add(parent, child, nameof(ResolutionStrategy.ChildWins));
                data.Add(parent, child, nameof(ResolutionStrategy.ParentWins));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SingleValueCombinations))]
    public void SingleValueExploration(string parentAction, string childAction, string strategyName)
    {
        var strategy = Enum.Parse<ResolutionStrategy>(strategyName);
        var service = NewService(Op.Create("e1", "Class"), Op.Set("e1", "name", "base"));

        ApplySingleAction(service, A, parentAction, "alice");
        ApplySingleAction(service, B, childAction, "bob");

        Dance(service, strategy);

        var scenario = $"single parent={parentAction} child={childAction} strategy={strategy}";
        AssertConverged(service, scenario);

        var element = service.GetModel(ModelService.PublicWorkspaceId).GetElement("e1");

        // Aliveness oracle.
        var parentDeletes = parentAction == "delete";
        var childDeletes = childAction == "delete";
        bool expectAlive = (parentDeletes, childDeletes) switch
        {
            (true, true) => false,
            (true, false) => childAction is "set-own" or "set-shared"
                ? strategy == ResolutionStrategy.ChildWins
                : false, // child made no constructive change: deletion stands
            (false, true) => parentAction is "set-own" or "set-shared"
                ? strategy == ResolutionStrategy.ParentWins
                : false,
            _ => true
        };

        Assert.Equal(expectAlive, element is not null);

        // Value oracle for the conflict-free and both-set cases (no deletes involved).
        if (!parentDeletes && !childDeletes && element is not null)
        {
            var value = element.GetProperty("name")?.SingleValue?.Content;
            var expected = (parentAction, childAction) switch
            {
                ("none", "none") => "base",
                ("none", "set-own") => "bob",
                ("none", "set-shared") => "alice-shared",
                ("none", "unset") => null,
                ("set-own", "none") => "alice",
                ("set-shared", "none") => "alice-shared",
                ("unset", "none") => null,
                ("set-own", "set-own") => strategy == ResolutionStrategy.ChildWins ? "bob" : "alice",
                ("set-own", "set-shared") => strategy == ResolutionStrategy.ChildWins ? "alice-shared" : "alice",
                ("set-shared", "set-own") => strategy == ResolutionStrategy.ChildWins ? "bob" : "alice-shared",
                ("set-shared", "set-shared") => "alice-shared", // identical concurrent values: pseudo
                ("set-own", "unset") => strategy == ResolutionStrategy.ChildWins ? null : "alice",
                ("set-shared", "unset") => strategy == ResolutionStrategy.ChildWins ? null : "alice-shared",
                ("unset", "set-own") => strategy == ResolutionStrategy.ChildWins ? "bob" : null,
                ("unset", "set-shared") => strategy == ResolutionStrategy.ChildWins ? "alice-shared" : null,
                ("unset", "unset") => null,
                _ => throw new InvalidOperationException(scenario)
            };

            Assert.Equal(expected, value);
        }
    }

    private static void ApplySingleAction(ModelService service, string workspace, string action, string ownValue)
    {
        switch (action)
        {
            case "set-own":
                service.Apply(workspace, Op.Set("e1", "name", ownValue));
                break;
            case "set-shared":
                service.Apply(workspace, Op.Set("e1", "name", "alice-shared"));
                break;
            case "unset":
                service.Apply(workspace, Op.Unset("e1", "name"));
                break;
            case "delete":
                service.Apply(workspace, Op.Delete("e1"));
                break;
        }
    }

    // ------------------------------------------------------------------ lists

    public static TheoryData<string, string, string> ListCombinations()
    {
        var parentActions = new[] { "none", "insert-after-a", "insert-head", "remove-a", "remove-b" };
        var childActions = new[] { "none", "insert-after-a", "insert-head", "insert-after-b", "remove-a", "remove-b" };
        var data = new TheoryData<string, string, string>();
        foreach (var parent in parentActions)
        {
            foreach (var child in childActions)
            {
                data.Add(parent, child, nameof(ResolutionStrategy.ChildWins));
                data.Add(parent, child, nameof(ResolutionStrategy.ParentWins));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ListCombinations))]
    public void ListExploration(string parentAction, string childAction, string strategyName)
    {
        var strategy = Enum.Parse<ResolutionStrategy>(strategyName);
        var service = NewService(
            Op.Create("e1"),
            Op.Insert("e1", "items", "i-a", "A", null),
            Op.Insert("e1", "items", "i-b", "B", "i-a"));

        ApplyListAction(service, A, parentAction, "X");
        ApplyListAction(service, B, childAction, "Y");

        Dance(service, strategy);

        var scenario = $"list parent={parentAction} child={childAction} strategy={strategy}";
        AssertConverged(service, scenario);

        var values = ModelAssert.ListValues(service.GetModel(ModelService.PublicWorkspaceId), "e1", "items");

        // Delete wins: removed base items are gone.
        if (parentAction == "remove-a" || childAction == "remove-a")
        {
            Assert.DoesNotContain("A", values);
        }
        else
        {
            Assert.Contains("A", values);
        }

        if (parentAction == "remove-b" || childAction == "remove-b")
        {
            Assert.DoesNotContain("B", values);
        }
        else
        {
            Assert.Contains("B", values);
        }

        // Inserts are never lost, even when their anchor was deleted.
        if (parentAction.StartsWith("insert"))
        {
            Assert.Contains("X", values);
        }

        if (childAction.StartsWith("insert"))
        {
            Assert.Contains("Y", values);
        }

        // Base order is preserved for surviving items.
        var ordered = values.ToList();
        if (ordered.Contains("A") && ordered.Contains("B"))
        {
            Assert.True(ordered.IndexOf("A") < ordered.IndexOf("B"), scenario);
        }
    }

    private static void ApplyListAction(ModelService service, string workspace, string action, string value)
    {
        switch (action)
        {
            case "insert-after-a":
                service.Apply(workspace, Op.Insert("e1", "items", $"i-{value}", value, "i-a"));
                break;
            case "insert-head":
                service.Apply(workspace, Op.Insert("e1", "items", $"i-{value}", value, null));
                break;
            case "insert-after-b":
                service.Apply(workspace, Op.Insert("e1", "items", $"i-{value}", value, "i-b"));
                break;
            case "remove-a":
                service.Apply(workspace, Op.RemoveItem("e1", "items", "i-a"));
                break;
            case "remove-b":
                service.Apply(workspace, Op.RemoveItem("e1", "items", "i-b"));
                break;
        }
    }

    // ------------------------------------------- three-workspace sequential sync

    [Fact]
    public void ThreeWorkspacesConvergeThroughSequentialSyncs()
    {
        var service = NewService(Op.Create("e1"), Op.Set("e1", "name", "base"));
        service.Checkout("C");
        service.Update("C");

        service.Apply(A, Op.Set("e1", "name", "alice"));
        service.Apply(B, Op.Set("e1", "name", "bob"));
        service.Apply("C", Op.Set("e1", "name", "carol"));

        Assert.True(service.Commit(A).Success);

        service.Update(B, ResolutionStrategy.ChildWins);
        Assert.True(service.Commit(B).Success);

        service.Update("C", ResolutionStrategy.ChildWins);
        Assert.True(service.Commit("C").Success);

        service.Update(A);
        service.Update(B);

        var publicModel = service.GetModel(ModelService.PublicWorkspaceId);
        ModelAssert.Equivalent(publicModel, service.GetModel(A));
        ModelAssert.Equivalent(publicModel, service.GetModel(B));
        ModelAssert.Equivalent(publicModel, service.GetModel("C"));
        Assert.Equal("carol", publicModel.GetElement("e1")!.GetProperty("name")!.SingleValue!.Content);
    }
}
