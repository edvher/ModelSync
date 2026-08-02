namespace ModelSync.Core;

/// <summary>
/// Deterministic, rule-based conflict resolution. Resolutions are expressed as
/// new operations appended to the updating workspace's branch; replaying them
/// re-asserts the winning outcome on every replica, which is what makes all
/// models converge.
/// </summary>
public static class ConflictResolver
{
    /// <summary>
    /// Creates the resolution operations for a conflict; empty when the
    /// conflict needs none (pseudo conflicts with commutative outcomes).
    /// <paramref name="childModel"/> must already contain the merged state
    /// (child delta plus public delta) so list re-anchoring can consult it.
    /// The deltas are needed for list conflicts: the winning inserts are
    /// re-executed together with every insert transitively anchored behind
    /// them, so whole inserted sequences keep their relative order everywhere.
    /// </summary>
    public static IReadOnlyList<Operation> CreateResolutions(
        Conflict conflict,
        ResolutionStrategy strategy,
        ModelState childModel,
        string workspaceId,
        IReadOnlyList<Operation> parentDelta,
        IReadOnlyList<Operation> childDelta)
    {
        if (!conflict.RequiresResolution)
        {
            return Array.Empty<Operation>();
        }

        switch (conflict.Category)
        {
            case ConflictCategory.ElementExistence:
                return new[] { ResolveElementExistence(conflict, strategy, childModel, workspaceId) };

            case ConflictCategory.ListAnchorDeleted:
                return ResolveAnchorDeleted(conflict, childModel, workspaceId, parentDelta, childDelta);

            case ConflictCategory.ListAnchorMoved:
                return ResolveAnchorMoved(conflict, workspaceId, parentDelta, childDelta);

            case ConflictCategory.ListOrder:
                return ResolveListOrder(conflict, strategy, workspaceId, parentDelta, childDelta);

            default:
                return new[] { Winner(conflict, strategy).CloneAsResolution(workspaceId) };
        }
    }

    private static Operation Winner(Conflict conflict, ResolutionStrategy strategy) =>
        strategy == ResolutionStrategy.ChildWins ? conflict.ChildOperation : conflict.ParentOperation;

    private static Operation Loser(Conflict conflict, ResolutionStrategy strategy) =>
        strategy == ResolutionStrategy.ChildWins ? conflict.ParentOperation : conflict.ChildOperation;

    /// <summary>
    /// Element-delete conflicts are binary: the resolution only decides whether
    /// the element stays deleted or stays alive; property changes are always
    /// applied. When the delete side loses, the delete is inverted into a
    /// resurrecting create.
    /// </summary>
    private static Operation ResolveElementExistence(
        Conflict conflict,
        ResolutionStrategy strategy,
        ModelState childModel,
        string workspaceId)
    {
        var winner = Winner(conflict, strategy);
        var loser = Loser(conflict, strategy);

        if (winner.Type == OperationType.DeleteElement)
        {
            return winner.CloneAsResolution(workspaceId);
        }

        if (loser.Type == OperationType.DeleteElement)
        {
            // The delete loses: invert it into a create that resurrects the element.
            var element = childModel.GetElementIncludingDeleted(loser.ElementId);
            return new Operation
            {
                Id = Guid.NewGuid(),
                Type = OperationType.CreateElement,
                WorkspaceId = workspaceId,
                ElementId = loser.ElementId,
                ElementTypeId = element?.TypeId,
                IsResolution = true
            };
        }

        // Create-vs-create with different types: re-assert the winning create.
        return winner.CloneAsResolution(workspaceId);
    }

    /// <summary>
    /// Competing inserts (same anchor, or the same item placed differently):
    /// re-execute the winner side's inserts at the anchor together with their
    /// transitive follower chains, in the winner delta's order.
    /// </summary>
    private static IReadOnlyList<Operation> ResolveListOrder(
        Conflict conflict,
        ResolutionStrategy strategy,
        string workspaceId,
        IReadOnlyList<Operation> parentDelta,
        IReadOnlyList<Operation> childDelta)
    {
        var winner = Winner(conflict, strategy);
        var winnerDelta = strategy == ResolutionStrategy.ChildWins ? childDelta : parentDelta;

        if (winner.Type != OperationType.InsertListItem)
        {
            return new[] { winner.CloneAsResolution(workspaceId) };
        }

        var closure = conflict.ConflictKey.StartsWith("anchor|", StringComparison.Ordinal)
            ? ConflictDetector.AnchorGroupClosure(winnerDelta, winner.ElementId, winner.PropertyName!, winner.AfterItemId)
            : ConflictDetector.InsertChainClosure(winnerDelta, winner);

        return closure.Count == 0
            ? new[] { winner.CloneAsResolution(workspaceId) }
            : closure.Select(op => op.CloneAsResolution(workspaceId)).ToList();
    }

    /// <summary>
    /// Insert-after-deleted has no binary choice: the dependent insert is kept
    /// and re-anchored onto the closest surviving predecessor; its follower
    /// chain is re-executed behind it.
    /// </summary>
    private static IReadOnlyList<Operation> ResolveAnchorDeleted(
        Conflict conflict,
        ModelState childModel,
        string workspaceId,
        IReadOnlyList<Operation> parentDelta,
        IReadOnlyList<Operation> childDelta)
    {
        var (dependent, dependentDelta) = DependentInsert(conflict, parentDelta, childDelta);
        if (dependent is null)
        {
            return Array.Empty<Operation>();
        }

        var property = childModel
            .GetElementIncludingDeleted(dependent.ElementId)?
            .GetProperty(dependent.PropertyName!);

        string? newAnchor = null;
        if (property is not null)
        {
            // The inserted node already sits right after the tombstoned anchor in
            // the merged state; its closest alive predecessor is the position-
            // preserving replacement anchor.
            newAnchor = property.FindNode(dependent.ItemId!) is not null
                ? property.FirstAlivePredecessor(dependent.ItemId!)
                : dependent.AfterItemId is not null
                    ? property.FirstAlivePredecessor(dependent.AfterItemId)
                    : null;
        }

        var closure = ConflictDetector.InsertChainClosure(dependentDelta, dependent);
        var result = new List<Operation>();
        foreach (var op in closure)
        {
            var clone = op.CloneAsResolution(workspaceId);
            if (string.Equals(op.ItemId, dependent.ItemId, StringComparison.Ordinal))
            {
                clone = clone with { AfterItemId = newAnchor };
            }

            result.Add(clone);
        }

        return result;
    }

    /// <summary>
    /// The anchor item was moved concurrently: re-execute the dependent insert
    /// (and its followers) so it follows the anchor's new position. There is no
    /// choice to make; the resolution is strategy-independent.
    /// </summary>
    private static IReadOnlyList<Operation> ResolveAnchorMoved(
        Conflict conflict,
        string workspaceId,
        IReadOnlyList<Operation> parentDelta,
        IReadOnlyList<Operation> childDelta)
    {
        var (dependent, dependentDelta) = DependentInsert(conflict, parentDelta, childDelta);
        if (dependent is null)
        {
            return Array.Empty<Operation>();
        }

        return ConflictDetector.InsertChainClosure(dependentDelta, dependent)
            .Select(op => op.CloneAsResolution(workspaceId))
            .ToList();
    }

    /// <summary>
    /// The dependent insert of an anchor conflict is the insert whose anchor is
    /// the other side's item (the removed or moved one).
    /// </summary>
    private static (Operation? Dependent, IReadOnlyList<Operation> Delta) DependentInsert(
        Conflict conflict,
        IReadOnlyList<Operation> parentDelta,
        IReadOnlyList<Operation> childDelta)
    {
        var parentOp = conflict.ParentOperation;
        var childOp = conflict.ChildOperation;

        if (childOp.Type == OperationType.InsertListItem &&
            string.Equals(childOp.AfterItemId, parentOp.ItemId, StringComparison.Ordinal))
        {
            return (childOp, childDelta);
        }

        if (parentOp.Type == OperationType.InsertListItem &&
            string.Equals(parentOp.AfterItemId, childOp.ItemId, StringComparison.Ordinal))
        {
            return (parentOp, parentDelta);
        }

        return (null, childDelta);
    }
}
