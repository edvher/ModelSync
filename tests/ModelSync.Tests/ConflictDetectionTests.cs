using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

/// <summary>
/// The complete detection catalog: REAL vs PSEUDO per property type,
/// classified as MMC/DMC/MDC/DDC with their resolution policies.
/// Parent = public delta, child = private delta.
/// </summary>
public class ConflictDetectionTests
{
    private static Conflict Single(IReadOnlyList<Conflict> conflicts) => Assert.Single(conflicts);

    // ------------------------------------------------------------ single value

    [Fact]
    public void SingleValue_SetSet_DifferentValues_IsRealMmc()
    {
        var conflicts = ConflictDetector.Detect(
            new[] { Op.Set("e1", "name", "scanHeartbeat") },
            new[] { Op.Set("e1", "name", "sampleHeartbeat") });

        var conflict = Single(conflicts);
        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(MergeConflictType.Mmc, conflict.MergeType);
        Assert.Equal(ConflictCategory.SingleValue, conflict.Category);
        Assert.Equal(ResolutionPolicy.Choose, conflict.Policy);
        Assert.True(conflict.RequiresResolution);
    }

    [Fact]
    public void SingleValue_SetSet_SameValue_IsPseudo()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Set("e1", "name", "same") },
            new[] { Op.Set("e1", "name", "same") }));

        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.Equal(ResolutionPolicy.Ignore, conflict.Policy);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void SingleValue_SetVsUnset_IsRealMdc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Set("e1", "name", "x") },
            new[] { Op.Unset("e1", "name") }));

        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(MergeConflictType.Mdc, conflict.MergeType);
        Assert.True(conflict.RequiresResolution);
    }

    [Fact]
    public void SingleValue_UnsetVsSet_IsRealDmc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Unset("e1", "name") },
            new[] { Op.Set("e1", "name", "x") }));

        Assert.Equal(MergeConflictType.Dmc, conflict.MergeType);
        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
    }

    [Fact]
    public void SingleValue_UnsetUnset_IsPseudoDdc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Unset("e1", "name") },
            new[] { Op.Unset("e1", "name") }));

        Assert.Equal(MergeConflictType.Ddc, conflict.MergeType);
        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void DifferentPropertiesDoNotConflict()
    {
        Assert.Empty(ConflictDetector.Detect(
            new[] { Op.Set("e1", "name", "x") },
            new[] { Op.Set("e1", "description", "y") }));
    }

    [Fact]
    public void DifferentElementsDoNotConflict()
    {
        Assert.Empty(ConflictDetector.Detect(
            new[] { Op.Set("e1", "name", "x") },
            new[] { Op.Set("e2", "name", "y") }));
    }

    // ------------------------------------------------------------------- sets

    [Fact]
    public void Set_AddAdd_SameValue_IsPseudoNoResolution()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.AddSet("e1", "tags", "a") },
            new[] { Op.AddSet("e1", "tags", "a") }));

        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void Set_AddRemove_SameValue_IsPseudoButNeedsResolution()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.AddSet("e1", "tags", "a") },
            new[] { Op.RemoveSet("e1", "tags", "a") }));

        // Never a true conflict (mutually exclusive preconditions) but
        // non-commutative: convergence needs a resolution operation.
        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.Equal(MergeConflictType.Mdc, conflict.MergeType);
        Assert.Equal(ResolutionPolicy.Merge, conflict.Policy);
        Assert.True(conflict.RequiresResolution);
    }

    [Fact]
    public void Set_DifferentValues_DoNotConflict()
    {
        Assert.Empty(ConflictDetector.Detect(
            new[] { Op.AddSet("e1", "tags", "a") },
            new[] { Op.RemoveSet("e1", "tags", "b") }));
    }

    // ------------------------------------------------------------------- maps

    [Fact]
    public void Map_PutPut_SameKeyDifferentValues_IsRealMmc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Put("e1", "attrs", "k", "A") },
            new[] { Op.Put("e1", "attrs", "k", "B") }));

        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(MergeConflictType.Mmc, conflict.MergeType);
        Assert.Equal(ConflictCategory.MapEntry, conflict.Category);
    }

    [Fact]
    public void Map_PutPut_SameKeySameValue_IsPseudo()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Put("e1", "attrs", "k", "A") },
            new[] { Op.Put("e1", "attrs", "k", "A") }));

        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void Map_PutVsRemove_SameKey_IsReal()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Put("e1", "attrs", "k", "A") },
            new[] { Op.RemoveMap("e1", "attrs", "k") }));

        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(MergeConflictType.Mdc, conflict.MergeType);
    }

    [Fact]
    public void Map_RemoveRemove_SameKey_IsPseudoDdc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.RemoveMap("e1", "attrs", "k") },
            new[] { Op.RemoveMap("e1", "attrs", "k") }));

        Assert.Equal(MergeConflictType.Ddc, conflict.MergeType);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void Map_DifferentKeys_DoNotConflict()
    {
        Assert.Empty(ConflictDetector.Detect(
            new[] { Op.Put("e1", "attrs", "k1", "A") },
            new[] { Op.Put("e1", "attrs", "k2", "B") }));
    }

    // ------------------------------------------------------------------ lists

    [Fact]
    public void List_InsertInsert_SameAnchor_IsRealOrderConflict()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Insert("e1", "items", "i-x", "X", "i-a") },
            new[] { Op.Insert("e1", "items", "i-y", "Y", "i-a") }));

        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(ConflictCategory.ListOrder, conflict.Category);
        Assert.Equal(ResolutionPolicy.Choose, conflict.Policy);
        Assert.True(conflict.RequiresResolution);
    }

    [Fact]
    public void List_IdenticalInsert_IsPseudo()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Insert("e1", "items", "i-x", "X", "i-a") },
            new[] { Op.Insert("e1", "items", "i-x", "X", "i-a") }));

        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void List_SameItemDifferentAnchors_IsReal()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Insert("e1", "items", "i-x", "X", "i-a") },
            new[] { Op.Insert("e1", "items", "i-x", "X", "i-b") }));

        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(ConflictCategory.ListOrder, conflict.Category);
    }

    [Fact]
    public void List_InsertAfterItemDeletedByParent_IsRealAnchorConflict()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.RemoveItem("e1", "items", "i-a") },
            new[] { Op.Insert("e1", "items", "i-y", "Y", "i-a") }));

        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(ConflictCategory.ListAnchorDeleted, conflict.Category);
        Assert.Equal(ResolutionPolicy.Merge, conflict.Policy);
        Assert.True(conflict.RequiresResolution);
    }

    [Fact]
    public void List_ChildRemovesAnchorOfParentInsert_IsRealAnchorConflict()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Insert("e1", "items", "i-y", "Y", "i-a") },
            new[] { Op.RemoveItem("e1", "items", "i-a") }));

        Assert.Equal(ConflictCategory.ListAnchorDeleted, conflict.Category);
        Assert.Equal(MergeConflictType.Mdc, conflict.MergeType);
        Assert.True(conflict.RequiresResolution);
    }

    [Fact]
    public void List_RemoveVsInsertOfSameItem_DeleteWins_Pseudo()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.RemoveItem("e1", "items", "i-a") },
            new[] { Op.Insert("e1", "items", "i-a", "A", "i-b") }));

        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void List_RemoveRemove_SameItem_IsPseudoDdc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.RemoveItem("e1", "items", "i-a") },
            new[] { Op.RemoveItem("e1", "items", "i-a") }));

        Assert.Equal(MergeConflictType.Ddc, conflict.MergeType);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void List_DifferentAnchors_DoNotConflict()
    {
        Assert.Empty(ConflictDetector.Detect(
            new[] { Op.Insert("e1", "items", "i-x", "X", "i-a") },
            new[] { Op.Insert("e1", "items", "i-y", "Y", "i-b") }));
    }

    // -------------------------------------------------------- element delete

    [Fact]
    public void Element_ParentDeleteVsChildModify_IsRealDmc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Delete("e1") },
            new[] { Op.Set("e1", "name", "x") }));

        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(MergeConflictType.Dmc, conflict.MergeType);
        Assert.Equal(ConflictCategory.ElementExistence, conflict.Category);
        Assert.True(conflict.RequiresResolution);
    }

    [Fact]
    public void Element_ParentDeleteVsChildDestructiveOp_IsPseudo()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Delete("e1") },
            new[] { Op.RemoveSet("e1", "tags", "a") }));

        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void Element_ChildDeleteVsParentModify_IsRealMdc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Set("e1", "name", "x") },
            new[] { Op.Delete("e1") }));

        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
        Assert.Equal(MergeConflictType.Mdc, conflict.MergeType);
        Assert.True(conflict.RequiresResolution);
    }

    [Fact]
    public void Element_DeleteDelete_IsPseudoDdc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Delete("e1") },
            new[] { Op.Delete("e1") }));

        Assert.Equal(MergeConflictType.Ddc, conflict.MergeType);
        Assert.Equal(ConflictSeverity.Pseudo, conflict.Severity);
        Assert.False(conflict.RequiresResolution);
    }

    [Fact]
    public void Element_ParentDeleteVsChildResurrect_IsRealDmc()
    {
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Delete("e1") },
            new[] { Op.Create("e1", "Class") }));

        Assert.Equal(MergeConflictType.Dmc, conflict.MergeType);
        Assert.Equal(ConflictSeverity.Real, conflict.Severity);
    }

    [Fact]
    public void Element_OneExistenceConflictPerElement()
    {
        var conflicts = ConflictDetector.Detect(
            new[] { Op.Delete("e1") },
            new[] { Op.Set("e1", "name", "x"), Op.Set("e1", "description", "y") });

        Assert.Single(conflicts, c => c.Category == ConflictCategory.ElementExistence && c.Severity == ConflictSeverity.Real);
    }

    // ------------------------------------------------------------ aggregates

    [Fact]
    public void LastOperationPerKeyWins()
    {
        // Parent sets name twice; only the last one is conflict-relevant.
        var last = Op.Set("e1", "name", "second");
        var conflict = Single(ConflictDetector.Detect(
            new[] { Op.Set("e1", "name", "first"), last },
            new[] { Op.Set("e1", "name", "other") }));

        Assert.Equal(last.Id, conflict.ParentOperation.Id);
    }

    [Fact]
    public void DetectionIsEmptyForDisjointEdits()
    {
        var parent = new[]
        {
            Op.Create("p1", "Class"),
            Op.Set("p1", "name", "Parent"),
            Op.AddSet("p1", "tags", "x")
        };
        var child = new[]
        {
            Op.Create("c1", "Class"),
            Op.Set("c1", "name", "Child"),
            Op.Put("c1", "attrs", "k", "v")
        };

        Assert.Empty(ConflictDetector.Detect(parent, child));
    }
}
