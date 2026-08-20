using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

/// <summary>
/// Full update/commit synchronization scenarios on the core service,
/// mirroring the running examples of the thesis (Alice = workspace A commits
/// first, Bob = workspace B updates, resolves, commits back).
/// Every scenario asserts convergence: after the sync dance, the public model,
/// both private models and a freshly replayed checkout are identical.
/// </summary>
public class SyncScenarioTests
{
    private const string A = "A";
    private const string B = "B";

    private static ModelService NewServiceWithBase(params Operation[] baseOps)
    {
        var service = new ModelService();
        service.Checkout(A);
        foreach (var op in baseOps)
        {
            service.Apply(A, op);
        }

        if (baseOps.Length > 0)
        {
            Assert.True(service.Commit(A).Success);
        }

        service.Checkout(B);
        var update = service.Update(B);
        Assert.True(baseOps.Length == 0 || update.WasUpToDate || update.PublicOperations.Count > 0);
        return service;
    }

    /// <summary>A commits first; B updates (resolving), commits; A updates. Everything must converge.</summary>
    private static UpdateResult SyncDance(ModelService service, ResolutionStrategy strategy)
    {
        Assert.True(service.Commit(A).Success);
        var bUpdate = service.Update(B, strategy);
        Assert.True(service.Commit(B).Success);
        service.Update(A, strategy);
        AssertAllConverged(service);
        return bUpdate;
    }

    private static void AssertAllConverged(ModelService service)
    {
        var publicModel = service.GetModel(ModelService.PublicWorkspaceId);
        ModelAssert.Equivalent(publicModel, service.GetModel(A));
        ModelAssert.Equivalent(publicModel, service.GetModel(B));

        // "Replaying all operations leads to the new model": a fresh checkout
        // reconstructed purely from the operation history equals the live state.
        var fresh = service.Checkout("fresh-" + Guid.NewGuid().ToString("N"));
        ModelAssert.Equivalent(publicModel, fresh);
    }

    // ---------------------------------------------------------------- basics

    [Fact]
    public void CommitFailsWhenBehindPublic()
    {
        var service = NewServiceWithBase(Op.Create("e1"));

        service.Apply(A, Op.Set("e1", "name", "fromA"));
        service.Apply(B, Op.Set("e1", "name", "fromB"));
        Assert.True(service.Commit(A).Success);

        var result = service.Commit(B);
        Assert.False(result.Success);
        Assert.Contains("update first", result.Reason);
    }

    [Fact]
    public void UpdateWithoutPublicChangesIsUpToDate()
    {
        var service = NewServiceWithBase(Op.Create("e1"));
        service.Apply(B, Op.Set("e1", "name", "local"));
        Assert.True(service.Update(B).WasUpToDate);
    }

    [Fact]
    public void FastForwardWithoutConcurrentChanges()
    {
        var service = NewServiceWithBase(Op.Create("e1"));
        service.Apply(A, Op.Set("e1", "name", "hello"));
        Assert.True(service.Commit(A).Success);

        var update = service.Update(B);
        Assert.False(update.WasUpToDate);
        Assert.Empty(update.Conflicts);
        Assert.Equal("hello", service.GetModel(B).GetElement("e1")!.GetProperty("name")!.SingleValue!.Content);
        AssertAllConverged(service);
    }

    // ------------------------------------------- thesis running example (MMC)

    [Fact]
    public void ConcurrentRename_ChildWins()
    {
        var service = NewServiceWithBase(Op.Create("m1", "Method"), Op.Set("m1", "name", "measureHeartbeat"));

        service.Apply(A, Op.Set("m1", "name", "scanHeartbeat"));   // Alice
        service.Apply(B, Op.Set("m1", "name", "sampleHeartbeat")); // Bob

        var update = SyncDance(service, ResolutionStrategy.ChildWins);
        var conflict = Assert.Single(update.Conflicts);
        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.NotNull(conflict.Resolution);

        Assert.Equal("sampleHeartbeat",
            service.GetModel(ModelService.PublicWorkspaceId).GetElement("m1")!.GetProperty("name")!.SingleValue!.Content);
    }

    [Fact]
    public void ConcurrentRename_ParentWins()
    {
        var service = NewServiceWithBase(Op.Create("m1", "Method"), Op.Set("m1", "name", "measureHeartbeat"));

        service.Apply(A, Op.Set("m1", "name", "scanHeartbeat"));
        service.Apply(B, Op.Set("m1", "name", "sampleHeartbeat"));

        SyncDance(service, ResolutionStrategy.ParentWins);

        Assert.Equal("scanHeartbeat",
            service.GetModel(ModelService.PublicWorkspaceId).GetElement("m1")!.GetProperty("name")!.SingleValue!.Content);
    }

    [Fact]
    public void IdenticalConcurrentEdits_PseudoConflictConvergesWithoutResolution()
    {
        var service = NewServiceWithBase(Op.Create("m1"));

        service.Apply(A, Op.Set("m1", "name", "same"));
        service.Apply(B, Op.Set("m1", "name", "same"));

        var update = SyncDance(service, ResolutionStrategy.ChildWins);
        var conflict = Assert.Single(update.Conflicts);
        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.Empty(update.ResolutionOperations);
    }

    // ------------------------------------------------------------------- maps

    [Theory]
    [InlineData(ResolutionStrategy.ChildWins, "B")]
    [InlineData(ResolutionStrategy.ParentWins, "A")]
    public void ConcurrentMapPut_ConvergesToWinner(ResolutionStrategy strategy, string expected)
    {
        var service = NewServiceWithBase(Op.Create("e1"));

        service.Apply(A, Op.Put("e1", "attrs", "k", "A"));
        service.Apply(B, Op.Put("e1", "attrs", "k", "B"));

        SyncDance(service, strategy);

        Assert.Equal(expected,
            service.GetModel(ModelService.PublicWorkspaceId).GetElement("e1")!.GetProperty("attrs")!.MapValues["k"].Content);
    }

    // ------------------------------------------------------------------- sets

    [Theory]
    [InlineData(ResolutionStrategy.ChildWins, false)]
    [InlineData(ResolutionStrategy.ParentWins, true)]
    public void SetAddVsRemove_ConvergesToWinner(ResolutionStrategy strategy, bool expectPresent)
    {
        var service = NewServiceWithBase(Op.Create("e1"), Op.AddSet("e1", "tags", "x"));

        service.Apply(A, Op.AddSet("e1", "tags", "x"));     // parent re-adds (was present)
        service.Apply(B, Op.RemoveSet("e1", "tags", "x"));  // child removes

        SyncDance(service, strategy);

        var present = service.GetModel(ModelService.PublicWorkspaceId)
            .GetElement("e1")!.GetProperty("tags")!.ContainsSetValue(PropertyValue.String("x"));
        Assert.Equal(expectPresent, present);
    }

    [Fact]
    public void DisjointSetEdits_MergeWithoutConflicts()
    {
        var service = NewServiceWithBase(Op.Create("e1"));

        service.Apply(A, Op.AddSet("e1", "tags", "fromA"));
        service.Apply(B, Op.AddSet("e1", "tags", "fromB"));

        var update = SyncDance(service, ResolutionStrategy.ChildWins);
        Assert.Empty(update.Conflicts);

        var tags = service.GetModel(ModelService.PublicWorkspaceId)
            .GetElement("e1")!.GetProperty("tags")!.SetValues.Select(v => v.Content).OrderBy(v => v).ToList();
        Assert.Equal(new[] { "fromA", "fromB" }, tags);
    }

    // ---------------------------------------------------------- element delete

    [Theory]
    [InlineData(ResolutionStrategy.ChildWins, true)]   // child modified -> element stays alive
    [InlineData(ResolutionStrategy.ParentWins, false)] // parent deleted -> element stays deleted
    public void ParentDeletesWhileChildModifies(ResolutionStrategy strategy, bool expectAlive)
    {
        var service = NewServiceWithBase(Op.Create("e1", "Class"), Op.Set("e1", "name", "original"));

        service.Apply(A, Op.Delete("e1"));
        service.Apply(B, Op.Set("e1", "name", "renamed"));

        var update = SyncDance(service, strategy);
        Assert.Contains(update.Conflicts, c => c.Category == ConflictCategory.ElementExistence && c.Severity == ConflictSeverity.Real);

        var element = service.GetModel(ModelService.PublicWorkspaceId).GetElement("e1");
        if (expectAlive)
        {
            Assert.NotNull(element);
            // Property changes are always applied; the resurrected element keeps them.
            Assert.Equal("renamed", element!.GetProperty("name")!.SingleValue!.Content);
            Assert.Equal("Class", element.TypeId);
        }
        else
        {
            Assert.Null(element);
        }
    }

    [Theory]
    [InlineData(ResolutionStrategy.ChildWins, false)]  // child deleted -> stays deleted
    [InlineData(ResolutionStrategy.ParentWins, true)]  // parent modified -> resurrected
    public void ChildDeletesWhileParentModifies(ResolutionStrategy strategy, bool expectAlive)
    {
        var service = NewServiceWithBase(Op.Create("e1", "Class"), Op.Set("e1", "name", "original"));

        service.Apply(A, Op.Set("e1", "name", "renamed"));
        service.Apply(B, Op.Delete("e1"));

        SyncDance(service, strategy);

        var element = service.GetModel(ModelService.PublicWorkspaceId).GetElement("e1");
        if (expectAlive)
        {
            Assert.NotNull(element);
            Assert.Equal("renamed", element!.GetProperty("name")!.SingleValue!.Content);
        }
        else
        {
            Assert.Null(element);
        }
    }

    [Fact]
    public void BothDelete_PseudoConflictConverges()
    {
        var service = NewServiceWithBase(Op.Create("e1"));

        service.Apply(A, Op.Delete("e1"));
        service.Apply(B, Op.Delete("e1"));

        var update = SyncDance(service, ResolutionStrategy.ChildWins);
        Assert.All(update.Conflicts, c => Assert.Equal(ConflictSeverity.Pseudo, c.Severity));
        Assert.Null(service.GetModel(ModelService.PublicWorkspaceId).GetElement("e1"));
    }

    // ------------------------------------------------------------------ lists

    [Theory]
    [InlineData(ResolutionStrategy.ChildWins, new[] { "A", "Y", "X", "B", "C" })]
    [InlineData(ResolutionStrategy.ParentWins, new[] { "A", "X", "Y", "B", "C" })]
    public void ConcurrentInsertsAtSameAnchor(ResolutionStrategy strategy, string[] expected)
    {
        // Thesis list POC: base [A,B,C]; Alice (parent) inserts X after A,
        // Bob (child) inserts Y after A. The winner's item ends up first.
        var service = NewServiceWithBase(
            Op.Create("e1"),
            Op.Insert("e1", "items", "i-a", "A", null),
            Op.Insert("e1", "items", "i-b", "B", "i-a"),
            Op.Insert("e1", "items", "i-c", "C", "i-b"));

        service.Apply(A, Op.Insert("e1", "items", "i-x", "X", "i-a"));
        service.Apply(B, Op.Insert("e1", "items", "i-y", "Y", "i-a"));

        var update = SyncDance(service, strategy);
        Assert.Contains(update.Conflicts, c => c.Category == ConflictCategory.ListOrder && c.Severity == ConflictSeverity.Real);

        Assert.Equal(expected, ModelAssert.ListValues(service.GetModel(ModelService.PublicWorkspaceId), "e1", "items"));
    }

    [Theory]
    [InlineData(ResolutionStrategy.ChildWins, new[] { "A", "X", "U", "V", "B", "C" })]
    [InlineData(ResolutionStrategy.ParentWins, new[] { "A", "U", "V", "X", "B", "C" })]
    public void InsertSequenceVsSingleInsertAtSameAnchor(ResolutionStrategy strategy, string[] expected)
    {
        // Thesis list conflict case 2: the parent inserted a chain U,V after A;
        // the child inserted X after A. Only the anchor collision conflicts and
        // the follower chain stays with its predecessor.
        var service = NewServiceWithBase(
            Op.Create("e1"),
            Op.Insert("e1", "items", "i-a", "A", null),
            Op.Insert("e1", "items", "i-b", "B", "i-a"),
            Op.Insert("e1", "items", "i-c", "C", "i-b"));

        service.Apply(A, Op.Insert("e1", "items", "i-u", "U", "i-a"));
        service.Apply(A, Op.Insert("e1", "items", "i-v", "V", "i-u"));
        service.Apply(B, Op.Insert("e1", "items", "i-x", "X", "i-a"));

        SyncDance(service, strategy);

        Assert.Equal(expected, ModelAssert.ListValues(service.GetModel(ModelService.PublicWorkspaceId), "e1", "items"));
    }

    [Fact]
    public void InsertAfterDeletedAnchor_IsReanchoredAndConverges()
    {
        // Thesis list conflict case 3: parent removes B, child inserts Y after B.
        // No binary choice: the insert survives, re-anchored to the closest
        // alive predecessor, so Y keeps its intended position.
        var service = NewServiceWithBase(
            Op.Create("e1"),
            Op.Insert("e1", "items", "i-a", "A", null),
            Op.Insert("e1", "items", "i-b", "B", "i-a"),
            Op.Insert("e1", "items", "i-c", "C", "i-b"));

        service.Apply(A, Op.RemoveItem("e1", "items", "i-b"));
        service.Apply(B, Op.Insert("e1", "items", "i-y", "Y", "i-b"));

        var update = SyncDance(service, ResolutionStrategy.ChildWins);
        var conflict = Assert.Single(update.Conflicts, c => c.Category == ConflictCategory.ListAnchorDeleted);
        Assert.NotNull(conflict.Resolution);
        Assert.Equal("i-a", conflict.Resolution!.AfterItemId);

        Assert.Equal(new[] { "A", "Y", "C" }, ModelAssert.ListValues(service.GetModel(ModelService.PublicWorkspaceId), "e1", "items"));
    }

    [Fact]
    public void ConcurrentRemoveAndInsertOfSameItem_DeleteWins()
    {
        var service = NewServiceWithBase(
            Op.Create("e1"),
            Op.Insert("e1", "items", "i-a", "A", null),
            Op.Insert("e1", "items", "i-b", "B", "i-a"));

        service.Apply(A, Op.RemoveItem("e1", "items", "i-b"));
        service.Apply(B, Op.RemoveItem("e1", "items", "i-b"));

        SyncDance(service, ResolutionStrategy.ChildWins);
        Assert.Equal(new[] { "A" }, ModelAssert.ListValues(service.GetModel(ModelService.PublicWorkspaceId), "e1", "items"));
    }

    // --------------------------------------------- multi-round synchronization

    [Fact]
    public void RepeatedConcurrentRounds_KeepConverging()
    {
        var service = NewServiceWithBase(Op.Create("e1"), Op.Set("e1", "name", "v0"));

        for (var round = 1; round <= 3; round++)
        {
            service.Apply(A, Op.Set("e1", "name", $"a{round}"));
            service.Apply(B, Op.Set("e1", "name", $"b{round}"));

            Assert.True(service.Commit(A).Success);
            service.Update(B, ResolutionStrategy.ChildWins);
            Assert.True(service.Commit(B).Success);
            service.Update(A, ResolutionStrategy.ChildWins);

            AssertAllConverged(service);
            Assert.Equal($"b{round}",
                service.GetModel(ModelService.PublicWorkspaceId).GetElement("e1")!.GetProperty("name")!.SingleValue!.Content);
        }
    }

    [Fact]
    public void PublicMovesAgainAfterUpdate_SecondUpdateDetectsNewConflicts()
    {
        // Case VIII of the thesis scenarios: the earlier update was only a
        // partial detection; new public changes must be re-detected.
        var service = NewServiceWithBase(Op.Create("e1"), Op.Set("e1", "name", "v0"));

        service.Apply(B, Op.Set("e1", "name", "bob"));

        service.Apply(A, Op.Set("e1", "description", "harmless"));
        Assert.True(service.Commit(A).Success);
        var first = service.Update(B, ResolutionStrategy.ChildWins);
        Assert.Empty(first.Conflicts);

        service.Apply(A, Op.Set("e1", "name", "alice"));
        Assert.True(service.Commit(A).Success);
        var second = service.Update(B, ResolutionStrategy.ChildWins);
        var conflict = Assert.Single(second.Conflicts);
        Assert.Equal(ConflictSeverity.Real, conflict.Severity);

        Assert.True(service.Commit(B).Success);
        service.Update(A);
        AssertAllConverged(service);
        Assert.Equal("bob",
            service.GetModel(ModelService.PublicWorkspaceId).GetElement("e1")!.GetProperty("name")!.SingleValue!.Content);
    }

    [Fact]
    public void ResolutionOperationsAreMarkedAndCommitted()
    {
        var service = NewServiceWithBase(Op.Create("e1"), Op.Set("e1", "name", "v0"));

        service.Apply(A, Op.Set("e1", "name", "alice"));
        service.Apply(B, Op.Set("e1", "name", "bob"));

        Assert.True(service.Commit(A).Success);
        var update = service.Update(B, ResolutionStrategy.ChildWins);
        var resolution = Assert.Single(update.ResolutionOperations);
        Assert.True(resolution.IsResolution);

        var commit = service.Commit(B);
        Assert.True(commit.Success);
        Assert.Contains(commit.CommittedOperations, op => op.Id == resolution.Id);
    }

    // -------------------------------------------------------------- awareness

    [Fact]
    public void AwarenessSeesBrewingConflictsBetweenSiblingsAndClearsAfterSync()
    {
        var service = NewServiceWithBase(Op.Create("e1"), Op.Set("e1", "name", "v0"));
        var awareness = new ConflictAwarenessService(service);

        service.Apply(A, Op.Set("e1", "name", "alice"));
        service.Apply(B, Op.Set("e1", "name", "bob"));

        // Sibling awareness (A <-> B) without any synchronization.
        var brewing = awareness.GetConflicts(A, B);
        var conflict = Assert.Single(brewing);
        Assert.Equal(ConflictSeverity.Real, conflict.Severity);

        // Public vs B as well, once A committed.
        Assert.True(service.Commit(A).Success);
        Assert.Single(awareness.GetConflicts(ModelService.PublicWorkspaceId, B));

        // After the full dance everything is in sync and awareness is clean.
        service.Update(B, ResolutionStrategy.ChildWins);
        Assert.True(service.Commit(B).Success);
        service.Update(A);
        Assert.Empty(awareness.GetConflicts(A, B));
        Assert.Empty(awareness.GetConflicts(ModelService.PublicWorkspaceId, B));
    }

    // ------------------------------------------------------------- validation

    [Fact]
    public void NewOperationsOnDeletedElementsAreRejected()
    {
        var service = NewServiceWithBase(Op.Create("e1"));
        service.Apply(A, Op.Delete("e1"));

        Assert.Throws<InvalidOperationException>(() => service.Apply(A, Op.Set("e1", "name", "x")));

        // But a create resurrects it and edits work again.
        service.Apply(A, Op.Create("e1"));
        service.Apply(A, Op.Set("e1", "name", "back"));
        Assert.Equal("back", service.GetModel(A).GetElement("e1")!.GetProperty("name")!.SingleValue!.Content);
    }

    [Fact]
    public void InsertWithUnknownAnchorIsRejected()
    {
        var service = NewServiceWithBase(Op.Create("e1"), Op.Insert("e1", "items", "i-a", "A", null));

        Assert.Throws<InvalidOperationException>(() =>
            service.Apply(A, Op.Insert("e1", "items", "i-x", "X", "nonexistent")));
    }
}
