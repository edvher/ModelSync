using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

/// <summary>
/// Regression tests recovered from the adversarial conflict-catalog audit.
/// Each case was misclassified (or missed entirely) before detection moved to
/// net-effect indexing: the detector now classifies a branch by where it ended
/// up ("Delete ≙ Modify* Delete"), not by every superseded intermediate step.
/// </summary>
public class RegressionClassificationTests
{
    private static IReadOnlyList<string> PubList(ModelService svc) =>
        ModelAssert.ListValues(svc.GetModel(ModelService.PublicWorkspaceId), "e1", "items");

    private static IReadOnlyList<string> BobList(ModelService svc) =>
        ModelAssert.ListValues(svc.GetModel("bob"), "e1", "items");

    // Same item, same anchor, DIFFERENT VALUES: a real MMC, like Single and Map.
    [Fact]
    public void SameItemSameAnchorDifferentValues_IsRealAndConverges()
    {
        var conflicts = ConflictDetector.Detect(
            new[] { Op.Insert("e1", "items", "i-x", "X1", "i-a") },
            new[] { Op.Insert("e1", "items", "i-x", "X2", "i-a") });

        var c = Assert.Single(conflicts);
        Assert.Equal(ConflictSeverity.Real, c.Severity);
        Assert.True(c.RequiresResolution);

        var svc = new ModelService();
        svc.Checkout("alice");
        svc.Apply("alice", Op.Create("e1", "Class"));
        svc.Apply("alice", Op.Insert("e1", "items", "i-a", "A", null));
        svc.Apply("alice", Op.Insert("e1", "items", "i-x", "X", "i-a"));
        Assert.True(svc.Commit("alice").Success);

        svc.Checkout("bob");
        svc.Apply("alice", Op.Insert("e1", "items", "i-x", "X1", "i-a")); // edit x in place
        Assert.True(svc.Commit("alice").Success);

        svc.Apply("bob", Op.Insert("e1", "items", "i-x", "X2", "i-a"));   // concurrent edit
        svc.Update("bob", ResolutionStrategy.ChildWins);
        Assert.True(svc.Commit("bob").Success);

        Assert.Equal(new[] { "A", "X2" }, BobList(svc));
        Assert.Equal(PubList(svc), BobList(svc));
    }

    // A child insert anchored on an item the parent MOVED must follow the move.
    [Fact]
    public void InsertAfterItemMovedByParent_IsDetectedAndConverges()
    {
        var conflicts = ConflictDetector.Detect(
            new[] { Op.Insert("e1", "items", "i-a", "A", "i-b") },   // parent moves a after b
            new[] { Op.Insert("e1", "items", "i-y", "Y", "i-a") });  // child inserts after a

        var c = Assert.Single(conflicts);
        Assert.Equal(ConflictCategory.ListAnchorMoved, c.Category);
        Assert.Equal(ConflictSeverity.Real, c.Severity);
        Assert.True(c.RequiresResolution);

        var svc = new ModelService();
        svc.Checkout("alice");
        svc.Apply("alice", Op.Create("e1", "Class"));
        svc.Apply("alice", Op.Insert("e1", "items", "i-a", "A", null));
        svc.Apply("alice", Op.Insert("e1", "items", "i-b", "B", "i-a"));
        Assert.True(svc.Commit("alice").Success); // [A, B]

        svc.Checkout("bob");
        svc.Apply("alice", Op.Insert("e1", "items", "i-a", "A", "i-b")); // move a after b -> [B, A]
        Assert.True(svc.Commit("alice").Success);

        svc.Apply("bob", Op.Insert("e1", "items", "i-y", "Y", "i-a"));   // -> [A, Y, B]
        var update = svc.Update("bob");
        Assert.Contains(update.Conflicts, x => x.Category == ConflictCategory.ListAnchorMoved);
        Assert.True(svc.Commit("bob").Success);

        Assert.Equal(new[] { "B", "A", "Y" }, PubList(svc));
        Assert.Equal(PubList(svc), BobList(svc));
    }

    // Parent delete vs child [Set, Delete]: the child's superseded Set must not
    // produce a real DMC — both branches netted the deletion.
    [Fact]
    public void ParentDelete_ChildSetThenDelete_IsOnlyPseudoDdc()
    {
        var conflicts = ConflictDetector.Detect(
            new[] { Op.Delete("e1") },
            new[] { Op.Set("e1", "name", "x"), Op.Delete("e1") });

        var c = Assert.Single(conflicts);
        Assert.Equal(MergeConflictType.Ddc, c.MergeType);
        Assert.Equal(ConflictSeverity.Pseudo, c.Severity);
        Assert.False(c.RequiresResolution);

        var svc = new ModelService();
        svc.Checkout("alice");
        svc.Apply("alice", Op.Create("e1", "Class"));
        Assert.True(svc.Commit("alice").Success);

        svc.Checkout("bob");
        svc.Apply("alice", Op.Delete("e1"));
        Assert.True(svc.Commit("alice").Success);

        svc.Apply("bob", Op.Set("e1", "name", "x"));
        svc.Apply("bob", Op.Delete("e1")); // bob's final intent: deleted
        svc.Update("bob", ResolutionStrategy.ChildWins);
        Assert.True(svc.Commit("bob").Success);

        Assert.Null(svc.GetModel("bob").GetElement("e1"));
        Assert.Null(svc.GetModel(ModelService.PublicWorkspaceId).GetElement("e1"));
    }

    // Parent [Set p, Unset p] nets to a destructive change; a concurrent child
    // delete is a pseudo conflict and the deletion stands under any strategy.
    [Fact]
    public void ParentSetThenUnset_ChildDelete_DeletionStands()
    {
        var conflicts = ConflictDetector.Detect(
            new[] { Op.Set("e1", "p", "x"), Op.Unset("e1", "p") },
            new[] { Op.Delete("e1") });

        var c = Assert.Single(conflicts);
        Assert.Equal(MergeConflictType.Mdc, c.MergeType);
        Assert.Equal(ConflictSeverity.Pseudo, c.Severity);
        Assert.False(c.RequiresResolution);

        var svc = new ModelService();
        svc.Checkout("alice");
        svc.Apply("alice", Op.Create("e1", "Class"));
        svc.Apply("alice", Op.Set("e1", "p", "v0"));
        Assert.True(svc.Commit("alice").Success);

        svc.Checkout("bob");
        svc.Apply("alice", Op.Set("e1", "p", "x"));
        svc.Apply("alice", Op.Unset("e1", "p"));
        Assert.True(svc.Commit("alice").Success);

        svc.Apply("bob", Op.Delete("e1"));
        svc.Update("bob", ResolutionStrategy.ParentWins);

        Assert.Null(svc.GetModel("bob").GetElement("e1"));
    }

    // Parent inserted then removed an item (revoked its own insert): no
    // surviving competitor, so a concurrent child insert at the same anchor
    // must not be flagged.
    [Fact]
    public void ParentRevokedInsert_NoConflictWithChildInsert()
    {
        Assert.Empty(ConflictDetector.Detect(
            new[] { Op.Insert("e1", "items", "i-x", "X", "i-a"), Op.RemoveItem("e1", "items", "i-x") },
            new[] { Op.Insert("e1", "items", "i-y", "Y", "i-a") }));
    }

    // But the canonical last-op-per-slot case still classifies: parent's net on
    // the property is destructive (Unset), child sets -> real DMC.
    [Fact]
    public void ParentSetThenUnset_ChildSet_IsRealDmc()
    {
        var conflicts = ConflictDetector.Detect(
            new[] { Op.Set("e1", "p", "x"), Op.Unset("e1", "p") },
            new[] { Op.Set("e1", "p", "y") });

        var c = Assert.Single(conflicts);
        Assert.Equal(MergeConflictType.Dmc, c.MergeType);
        Assert.Equal(ConflictSeverity.Real, c.Severity);
    }
}
