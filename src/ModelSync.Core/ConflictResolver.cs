namespace ModelSync.Core;

/// <summary>
/// Deterministic, rule-based conflict resolution. Resolutions are expressed as
/// new operations appended to the updating workspace's branch; replaying them
/// re-asserts the winning outcome on every replica, which is what makes all
/// models converge to the same state.
/// </summary>
public static class ConflictResolver
{
    /// <summary>
    /// Creates the resolution operations for a conflict; empty when the
    /// conflict needs none (pseudo conflicts with commutative outcomes).
    /// <paramref name="childModel"/> must already contain the merged state
    /// (child delta plus public delta) so list re-anchoring can consult it.
    /// The deltas are needed for list-order conflicts: the winning insert is
    /// re-executed together with the follower chain anchored behind it, so the
    /// whole inserted sequence keeps its relative order on every replica.
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
            case ConflictCategory.ListAnchorDeleted:
            {
                var resolution = ReanchorInsert(conflict, childModel, workspaceId);
                return resolution is null ? Array.Empty<Operation>() : new[] { resolution };
            }

            case ConflictCategory.ElementExistence:
                return new[] { ResolveElementExistence(conflict, strategy, childModel, workspaceId) };

            case ConflictCategory.ListOrder:
            {
                var winner = Winner(conflict, strategy);
                if (winner.Type != OperationType.InsertListItem)
                {
                    return new[] { winner.CloneAsResolution(workspaceId) };
                }

                var winnerDelta = strategy == ResolutionStrategy.ChildWins ? childDelta : parentDelta;
                return CollectInsertChain(winner, winnerDelta)
                    .Select(op => op.CloneAsResolution(workspaceId))
                    .ToList();
            }

            default:
                return new[] { Winner(conflict, strategy).CloneAsResolution(workspaceId) };
        }
    }

    /// <summary>
    /// The winning insert plus every insert of the same delta transitively
    /// anchored behind it ("clone the sequence that follows the conflicting
    /// item"), in chain order.
    /// </summary>
    private static List<Operation> CollectInsertChain(Operation winner, IReadOnlyList<Operation> winnerDelta)
    {
        var chain = new List<Operation> { winner };
        var seen = new HashSet<string> { winner.ItemId! };
        var lastItemId = winner.ItemId;

        var found = true;
        while (found)
        {
            found = false;
            foreach (var op in winnerDelta)
            {
                if (op.Type == OperationType.InsertListItem &&
                    op.ElementId == winner.ElementId &&
                    string.Equals(op.PropertyName, winner.PropertyName, StringComparison.Ordinal) &&
                    string.Equals(op.AfterItemId, lastItemId, StringComparison.Ordinal) &&
                    op.ItemId is not null &&
                    seen.Add(op.ItemId))
                {
                    chain.Add(op);
                    lastItemId = op.ItemId;
                    found = true;
                    break;
                }
            }
        }

        return chain;
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
    /// Insert-after-deleted has no binary choice: the insert is kept and
    /// re-anchored onto the closest surviving predecessor, so the final list
    /// state no longer depends on the tombstoned anchor.
    /// </summary>
    private static Operation? ReanchorInsert(Conflict conflict, ModelState childModel, string workspaceId)
    {
        var insert = conflict.ParentOperation.Type == OperationType.InsertListItem
            ? conflict.ParentOperation
            : conflict.ChildOperation;

        if (insert.Type != OperationType.InsertListItem)
        {
            return null;
        }

        var property = childModel
            .GetElementIncludingDeleted(insert.ElementId)?
            .GetProperty(insert.PropertyName!);

        string? newAnchor = null;
        if (property is not null)
        {
            // The inserted node already sits right after the tombstoned anchor in
            // the merged state; its closest alive predecessor is the position-
            // preserving replacement anchor.
            newAnchor = property.FindNode(insert.ItemId!) is not null
                ? property.FirstAlivePredecessor(insert.ItemId!)
                : insert.AfterItemId is not null
                    ? property.FirstAlivePredecessor(insert.AfterItemId)
                    : null;
        }

        return insert with
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            AfterItemId = newAnchor,
            IsResolution = true,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}
