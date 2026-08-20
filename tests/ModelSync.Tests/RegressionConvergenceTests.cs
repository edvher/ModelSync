using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

/// <summary>Regression tests recovered from the adversarial convergence audit:
/// each scenario exposed a real divergence before the net-effect detector and
/// closure-based list resolution were introduced.</summary>
public class RegressionConvergenceTests
{
    private const string A = "A";
    private const string B = "B";

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

    private static void AssertConverged(ModelService service)
    {
        var publicModel = service.GetModel(ModelService.PublicWorkspaceId);
        ModelAssert.Equivalent(publicModel, service.GetModel(A));
        ModelAssert.Equivalent(publicModel, service.GetModel(B));
        var fresh = service.Checkout("fresh-" + Guid.NewGuid().ToString("N"));
        ModelAssert.Equivalent(publicModel, fresh);
    }

    private static string Dump(ModelService service, string ws) =>
        ws + ": [" + string.Join(",", ModelAssert.ListValues(service.GetModel(ws), "e1", "items")) + "]";

    // 1. Winner insert has TWO followers anchored on it (branching follower chain).
    [Fact]
    public void Repro1_BranchingFollowerChain_ChildWins()
    {
        var s = NewService(Op.Create("e1"), Op.Insert("e1", "items", "i-a", "a", null));

        s.Apply(A, Op.Insert("e1", "items", "i-x", "x", "i-a"));

        s.Apply(B, Op.Insert("e1", "items", "i-w", "w", "i-a"));
        s.Apply(B, Op.Insert("e1", "items", "i-p", "p", "i-w"));
        s.Apply(B, Op.Insert("e1", "items", "i-q", "q", "i-w"));

        Dance(s, ResolutionStrategy.ChildWins);

        var msg = Dump(s, ModelService.PublicWorkspaceId) + " " + Dump(s, A) + " " + Dump(s, B);
        try { AssertConverged(s); }
        catch (Exception ex) { throw new Xunit.Sdk.XunitException(msg + " => " + ex.Message); }
    }

    // 2. TWO parent inserts at the same anchor: keyed index keeps only the last one.
    [Fact]
    public void Repro2_TwoParentInsertsSameAnchor_ParentWins()
    {
        var s = NewService(Op.Create("e1"), Op.Insert("e1", "items", "i-a", "a", null));

        s.Apply(A, Op.Insert("e1", "items", "i-w", "w", "i-a"));
        s.Apply(A, Op.Insert("e1", "items", "i-z", "z", "i-a"));

        s.Apply(B, Op.Insert("e1", "items", "i-c", "c", "i-a"));

        Dance(s, ResolutionStrategy.ParentWins);

        var msg = Dump(s, ModelService.PublicWorkspaceId) + " " + Dump(s, A) + " " + Dump(s, B);
        try { AssertConverged(s); }
        catch (Exception ex) { throw new Xunit.Sdk.XunitException(msg + " => " + ex.Message); }
    }

    // 3. Child deletes then resurrects; parent only sets a property. Both branch
    //    end-states have the element ALIVE, so no strategy should kill it.
    [Fact]
    public void Repro3_ChildDeleteThenResurrect_ChildWins_ElementStaysAlive()
    {
        var s = NewService(Op.Create("e1", "Class"), Op.Set("e1", "name", "base"));

        s.Apply(A, Op.Set("e1", "name", "alice"));

        s.Apply(B, Op.Delete("e1"));
        s.Apply(B, Op.Create("e1", "Class"));

        Dance(s, ResolutionStrategy.ChildWins);

        AssertConverged(s);
        var pub = s.GetModel(ModelService.PublicWorkspaceId);
        Assert.NotNull(pub.GetElement("e1"));
    }

    // 4. Awareness cache staleness: an update that appends no resolutions moves
    //    neither head (only reattaches), so the cache never invalidates.
    [Fact]
    public void Repro4_AwarenessCacheStale_AfterPseudoOnlyUpdate()
    {
        var s = NewService(Op.Create("e1"), Op.Set("e1", "name", "base"));
        var awareness = new ConflictAwarenessService(s);

        s.Apply(A, Op.Set("e1", "name", "same"));
        Assert.True(s.Commit(A).Success);

        s.Apply(B, Op.Set("e1", "name", "same"));

        var before = awareness.GetConflicts(ModelService.PublicWorkspaceId, B);
        Assert.Single(before);
        Assert.Equal(ConflictSeverity.Pseudo, before[0].Severity);

        var result = s.Update(B, ResolutionStrategy.ChildWins);
        Assert.False(result.WasUpToDate);
        Assert.Empty(result.ResolutionOperations);

        // B is now fully synchronized with P; there is nothing brewing anymore.
        var after = awareness.GetConflicts(ModelService.PublicWorkspaceId, B);
        Assert.Empty(after);
    }

    // 5. Parent inserted w after a, then MOVED it after b. Stale anchor entry
    //    still pairs the superseded insert; ParentWins resolution re-executes it
    //    and undoes the parent's own move.
    [Fact]
    public void Repro5_ParentMovedOwnItem_ParentWins_MoveSurvives()
    {
        var s = NewService(
            Op.Create("e1"),
            Op.Insert("e1", "items", "i-a", "a", null),
            Op.Insert("e1", "items", "i-b", "b", "i-a"));

        s.Apply(A, Op.Insert("e1", "items", "i-w", "w", "i-a"));
        s.Apply(A, Op.Insert("e1", "items", "i-w", "w", "i-b")); // move w behind b

        s.Apply(B, Op.Insert("e1", "items", "i-c", "c", "i-a"));

        Dance(s, ResolutionStrategy.ParentWins);

        AssertConverged(s);
        var values = ModelAssert.ListValues(s.GetModel(ModelService.PublicWorkspaceId), "e1", "items");
        // Parent's final state has w AFTER b; under ParentWins that must survive.
        Assert.True(values.ToList().IndexOf("w") > values.ToList().IndexOf("b"),
            "expected w after b, got [" + string.Join(",", values) + "]");
    }

    // 6. Sanity: child removes an item it inserted itself, with a follower.
    [Fact]
    public void Sanity6_ChildRemovesOwnInsert_WithFollower()
    {
        var s = NewService(Op.Create("e1"), Op.Insert("e1", "items", "i-a", "a", null));

        s.Apply(A, Op.Insert("e1", "items", "i-x", "x", "i-a"));

        s.Apply(B, Op.Insert("e1", "items", "i-w", "w", "i-a"));
        s.Apply(B, Op.Insert("e1", "items", "i-p", "p", "i-w"));
        s.Apply(B, Op.RemoveItem("e1", "items", "i-w"));

        Dance(s, ResolutionStrategy.ChildWins);
        AssertConverged(s);
    }

    // 7. Sanity: three workspaces inserting at the same anchor, sequential syncs.
    [Fact]
    public void Sanity7_ThreeWorkspacesSameAnchor()
    {
        var s = NewService(Op.Create("e1"), Op.Insert("e1", "items", "i-a", "a", null));
        s.Checkout("C");
        s.Update("C");

        s.Apply(A, Op.Insert("e1", "items", "i-x", "x", "i-a"));
        s.Apply(B, Op.Insert("e1", "items", "i-y", "y", "i-a"));
        s.Apply("C", Op.Insert("e1", "items", "i-z", "z", "i-a"));

        Assert.True(s.Commit(A).Success);
        s.Update(B, ResolutionStrategy.ChildWins);
        Assert.True(s.Commit(B).Success);
        s.Update("C", ResolutionStrategy.ChildWins);
        Assert.True(s.Commit("C").Success);
        s.Update(A);
        s.Update(B);

        var pub = s.GetModel(ModelService.PublicWorkspaceId);
        ModelAssert.Equivalent(pub, s.GetModel(A));
        ModelAssert.Equivalent(pub, s.GetModel(B));
        ModelAssert.Equivalent(pub, s.GetModel("C"));
        var fresh = s.Checkout("fresh-" + Guid.NewGuid().ToString("N"));
        ModelAssert.Equivalent(pub, fresh);
    }

    // 8. Sanity: updating twice with public moving in between (resolutions in child delta).
    [Fact]
    public void Sanity8_UpdateTwice_PublicMovesInBetween()
    {
        var s = NewService(Op.Create("e1"), Op.Insert("e1", "items", "i-a", "a", null));

        s.Apply(A, Op.Insert("e1", "items", "i-x", "x", "i-a"));
        Assert.True(s.Commit(A).Success);

        s.Apply(B, Op.Insert("e1", "items", "i-w", "w", "i-a"));
        s.Update(B, ResolutionStrategy.ChildWins);

        s.Apply(A, Op.Insert("e1", "items", "i-y", "y", "i-a"));
        Assert.True(s.Commit(A).Success);

        s.Update(B, ResolutionStrategy.ChildWins);
        Assert.True(s.Commit(B).Success);
        s.Update(A);

        var pub = s.GetModel(ModelService.PublicWorkspaceId);
        ModelAssert.Equivalent(pub, s.GetModel(A));
        ModelAssert.Equivalent(pub, s.GetModel(B));
        var fresh = s.Checkout("fresh-" + Guid.NewGuid().ToString("N"));
        ModelAssert.Equivalent(pub, fresh);
    }

    private static void AssertConvergedIncludingTombstones(ModelService service)
    {
        var publicModel = service.GetModel(ModelService.PublicWorkspaceId);
        ModelAssert.EquivalentIncludingTombstones(publicModel, service.GetModel(A));
        ModelAssert.EquivalentIncludingTombstones(publicModel, service.GetModel(B));
        var fresh = service.Checkout("fresh-" + Guid.NewGuid().ToString("N"));
        ModelAssert.EquivalentIncludingTombstones(publicModel, fresh);
    }

    // 9. Both sides edit a property, then both delete the element (DDC pseudo —
    //    no resolution). Without re-executing the private delta on update, the
    //    replicas would keep divergent property values on the tombstone; a
    //    later resurrect would surface the divergence with no conflict left to
    //    detect it. The update replay-order fix must keep even hidden state
    //    identical, so the resurrect round stays convergent.
    [Fact]
    public void Repro9_ResurrectAfterDivergentTombstoneEdits_Ddc()
    {
        var s = NewService(Op.Create("e1"), Op.Set("e1", "name", "base"));

        s.Apply(A, Op.Set("e1", "name", "alice"));
        s.Apply(A, Op.Delete("e1"));
        s.Apply(B, Op.Set("e1", "name", "bob"));
        s.Apply(B, Op.Delete("e1"));

        Dance(s, ResolutionStrategy.ChildWins);
        AssertConvergedIncludingTombstones(s);
        Assert.Null(s.GetModel(ModelService.PublicWorkspaceId).GetElement("e1"));

        // The resurrect: one side recreates the element; the tombstone's
        // retained properties become visible again — identically everywhere.
        s.Apply(A, Op.Create("e1"));
        Dance(s, ResolutionStrategy.ChildWins);
        AssertConvergedIncludingTombstones(s);

        var element = s.GetModel(ModelService.PublicWorkspaceId).GetElement("e1");
        Assert.NotNull(element);
        // Canonical replay order: public delta (alice, delete), then B's
        // re-executed delta (bob, delete) — the tombstone kept "bob".
        Assert.Equal("bob", element!.GetProperty("name")!.SingleValue!.Content);
    }

    // 10. Delete wins over a concurrent constructive edit (DMC, ParentWins);
    //     a later resurrect must expose the same retained value on every
    //     replica, including a fresh replay.
    [Fact]
    public void Repro10_ResurrectAfterDeleteWins_ExposesIdenticalRetainedState()
    {
        var s = NewService(Op.Create("e1"), Op.Set("e1", "name", "base"));

        s.Apply(A, Op.Set("e1", "name", "alice"));
        s.Apply(A, Op.Delete("e1"));
        s.Apply(B, Op.Set("e1", "grade", "A+"));

        Dance(s, ResolutionStrategy.ParentWins);
        AssertConvergedIncludingTombstones(s);
        Assert.Null(s.GetModel(ModelService.PublicWorkspaceId).GetElement("e1"));

        s.Apply(B, Op.Create("e1"));
        Dance(s, ResolutionStrategy.ParentWins);
        AssertConvergedIncludingTombstones(s);

        var element = s.GetModel(ModelService.PublicWorkspaceId).GetElement("e1");
        Assert.NotNull(element);
        Assert.Equal("alice", element!.GetProperty("name")!.SingleValue!.Content);
        Assert.Equal("A+", element.GetProperty("grade")!.SingleValue!.Content);
    }
}
