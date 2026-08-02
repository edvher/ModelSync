namespace ModelSync.Core;

/// <summary>
/// Incremental, operation-based conflict detection between two divergent
/// operation sequences (the public/parent delta and the private/child delta
/// after their branching point).
///
/// Detection is O(n + m): each delta is indexed once by its NET effects —
/// the last operation per element lifecycle, per property slot, per set
/// member, per map key and per list item — and the two indexes are joined on
/// their keys. Net indexing implements the thesis footnote "Delete ≙
/// (Modify*) Delete": a branch is classified by where it ended up, not by
/// every intermediate operation it took.
/// </summary>
public static class ConflictDetector
{
    /// <summary>The per-delta net index used to join the two sides.</summary>
    internal sealed class DeltaIndex
    {
        /// <summary>Last Create/Delete per element.</summary>
        public Dictionary<string, Operation> NetElement { get; } = new(StringComparer.Ordinal);

        /// <summary>Last operation per keyed slot (single, set member, map key, list item).</summary>
        public Dictionary<string, Operation> NetKeyed { get; } = new(StringComparer.Ordinal);

        /// <summary>Net list inserts grouped by anchor key, in delta order.</summary>
        public Dictionary<string, List<Operation>> AnchorGroups { get; } = new(StringComparer.Ordinal);

        /// <summary>Elements touched by property operations.</summary>
        public HashSet<string> TouchedElements { get; } = new(StringComparer.Ordinal);

        /// <summary>The raw delta, needed by the resolver for chain closure.</summary>
        public IReadOnlyList<Operation> Delta { get; }

        /// <summary>Last insert per list item, even when a later remove superseded it.</summary>
        public Dictionary<string, Operation> LastInsertPerItem { get; } = new(StringComparer.Ordinal);

        public DeltaIndex(IReadOnlyList<Operation> delta)
        {
            Delta = delta;
            foreach (var op in delta)
            {
                if (op.IsElementOperation)
                {
                    NetElement[op.ElementId] = op;
                }
                else if (op.IsPropertyOperation)
                {
                    TouchedElements.Add(op.ElementId);
                    NetKeyed[KeyOf(op)] = op;
                    if (op.Type == OperationType.InsertListItem)
                    {
                        LastInsertPerItem[KeyOf(op)] = op;
                    }
                }
            }

            // "Carriers": items that were net-removed but still anchor surviving
            // inserts (directly or transitively). Their tombstone position still
            // matters, so their last insert keeps competing at its anchor.
            var carriers = new HashSet<string>(StringComparer.Ordinal);
            var work = new Queue<Operation>(NetKeyed.Values.Where(op => op.Type == OperationType.InsertListItem));
            while (work.Count > 0)
            {
                var op = work.Dequeue();
                if (op.AfterItemId is null)
                {
                    continue;
                }

                var anchorItemKey = ListItemKey(op.ElementId, op.PropertyName!, op.AfterItemId);
                if (NetKeyed.GetValueOrDefault(anchorItemKey)?.Type == OperationType.RemoveListItem &&
                    LastInsertPerItem.TryGetValue(anchorItemKey, out var carrierInsert) &&
                    carriers.Add(anchorItemKey))
                {
                    work.Enqueue(carrierInsert);
                }
            }

            // Build anchor groups from net list inserts plus carrier inserts: an
            // insert superseded by a later move of the same item no longer
            // competes for its old anchor, and a removed item competes only when
            // followers still depend on its position.
            foreach (var op in delta)
            {
                if (op.Type != OperationType.InsertListItem)
                {
                    continue;
                }

                var itemKey = KeyOf(op);
                var isNetInsert = ReferenceEquals(NetKeyed[itemKey], op);
                var isCarrier = carriers.Contains(itemKey) && ReferenceEquals(LastInsertPerItem[itemKey], op);
                if (!isNetInsert && !isCarrier)
                {
                    continue;
                }

                var key = AnchorKey(op.ElementId, op.PropertyName!, op.AfterItemId);
                if (!AnchorGroups.TryGetValue(key, out var group))
                {
                    group = new List<Operation>();
                    AnchorGroups[key] = group;
                }

                group.Add(op);
            }
        }

        /// <summary>
        /// The element's net property modification character, derived from the
        /// net keyed operations: constructive if any surviving slot operation
        /// adds or changes content. Returns the operation used for reporting.
        /// </summary>
        public (Operation? Constructive, Operation? Destructive) NetTouch(string elementId)
        {
            Operation? constructive = null;
            Operation? destructive = null;
            foreach (var op in NetKeyed.Values)
            {
                if (!string.Equals(op.ElementId, elementId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (op.IsConstructive)
                {
                    constructive = op;
                }
                else
                {
                    destructive = op;
                }
            }

            return (constructive, destructive);
        }
    }

    public static IReadOnlyList<Conflict> Detect(
        IReadOnlyList<Operation> parentDelta,
        IReadOnlyList<Operation> childDelta)
    {
        var parent = new DeltaIndex(parentDelta);
        var child = new DeltaIndex(childDelta);

        var conflicts = new List<Conflict>();
        DetectElementExistence(parent, child, conflicts);
        DetectKeyedSlots(parent, child, conflicts);
        DetectListAnchors(parent, child, conflicts);
        return conflicts;
    }

    /// <summary>
    /// Resolver hook: the winner-side net inserts competing for an anchor plus
    /// every net insert transitively anchored behind them, in delta order.
    /// Re-executing this closure re-asserts the winner's whole inserted
    /// sequence at the anchor, which is what makes replicas converge.
    /// </summary>
    public static IReadOnlyList<Operation> AnchorGroupClosure(
        IReadOnlyList<Operation> winnerDelta,
        string elementId,
        string propertyName,
        string? anchorItemId)
    {
        var index = new DeltaIndex(winnerDelta);
        var seeds = index.AnchorGroups.GetValueOrDefault(AnchorKey(elementId, propertyName, anchorItemId))
                    ?? new List<Operation>();
        return InsertClosure(index, seeds);
    }

    /// <summary>
    /// Resolver hook: one dependent insert plus its transitive followers.
    /// </summary>
    public static IReadOnlyList<Operation> InsertChainClosure(
        IReadOnlyList<Operation> delta,
        Operation insert)
    {
        var index = new DeltaIndex(delta);
        var net = index.NetKeyed.GetValueOrDefault(KeyOf(insert));
        var seed = net is { Type: OperationType.InsertListItem } ? net : insert;
        return InsertClosure(index, new List<Operation> { seed });
    }

    private static IReadOnlyList<Operation> InsertClosure(DeltaIndex index, List<Operation> seeds)
    {
        if (seeds.Count == 0)
        {
            return Array.Empty<Operation>();
        }

        var collected = new List<Operation>(seeds);
        var itemIds = new HashSet<string>(seeds.Select(op => op.ItemId!), StringComparer.Ordinal);

        var changed = true;
        while (changed)
        {
            changed = false;
            // Followers are found among all last inserts (including inserts of
            // removed items): re-executing a tombstoned insert is harmless and
            // keeps the chain behind it normalizable.
            foreach (var op in index.LastInsertPerItem.Values)
            {
                if (op.ElementId == seeds[0].ElementId &&
                    string.Equals(op.PropertyName, seeds[0].PropertyName, StringComparison.Ordinal) &&
                    op.AfterItemId is not null &&
                    itemIds.Contains(op.AfterItemId) &&
                    itemIds.Add(op.ItemId!))
                {
                    collected.Add(op);
                    changed = true;
                }
            }
        }

        // Re-execute in original delta order so anchors always exist when needed.
        var order = new Dictionary<Guid, int>();
        for (var i = 0; i < index.Delta.Count; i++)
        {
            order[index.Delta[i].Id] = i;
        }

        return collected.OrderBy(op => order.GetValueOrDefault(op.Id, int.MaxValue)).ToList();
    }

    // ------------------------------------------------------------------------
    // Element existence: one conflict per element, decided by NET states.
    // ------------------------------------------------------------------------

    private static void DetectElementExistence(DeltaIndex parent, DeltaIndex child, List<Conflict> conflicts)
    {
        var elements = new HashSet<string>(parent.NetElement.Keys, StringComparer.Ordinal);
        elements.UnionWith(child.NetElement.Keys);

        foreach (var elementId in elements.OrderBy(id => id, StringComparer.Ordinal))
        {
            var pOp = parent.NetElement.GetValueOrDefault(elementId);
            var cOp = child.NetElement.GetValueOrDefault(elementId);
            var key = ElementKey(elementId);

            var pDeleted = pOp?.Type == OperationType.DeleteElement;
            var cDeleted = cOp?.Type == OperationType.DeleteElement;

            if (pOp is not null && cOp is not null)
            {
                if (pDeleted && cDeleted)
                {
                    conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Ddc,
                        ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, pOp, cOp, false, key));
                }
                else if (pDeleted) // child net-created/resurrected
                {
                    conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Dmc,
                        ConflictSeverity.Real, ResolutionPolicy.Choose, pOp, cOp, true, key));
                }
                else if (cDeleted) // parent net-created/resurrected
                {
                    conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Mdc,
                        ConflictSeverity.Real, ResolutionPolicy.Choose, pOp, cOp, true, key));
                }
                else
                {
                    var sameType = string.Equals(pOp.ElementTypeId, cOp.ElementTypeId, StringComparison.Ordinal);
                    conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Mmc,
                        sameType ? ConflictSeverity.Pseudo : ConflictSeverity.Real,
                        sameType ? ResolutionPolicy.Ignore : ResolutionPolicy.Choose,
                        pOp, cOp, !sameType, key));
                }

                continue;
            }

            if (pDeleted)
            {
                // Parent deleted; the child's fate depends on its net property touches.
                var (constructive, destructive) = child.NetTouch(elementId);
                if (constructive is not null)
                {
                    conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Dmc,
                        ConflictSeverity.Real, ResolutionPolicy.Choose, pOp!, constructive, true, key));
                }
                else if (destructive is not null)
                {
                    conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Dmc,
                        ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, pOp!, destructive, false, key));
                }
            }
            else if (cDeleted)
            {
                var (constructive, destructive) = parent.NetTouch(elementId);
                if (constructive is not null)
                {
                    conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Mdc,
                        ConflictSeverity.Real, ResolutionPolicy.Choose, constructive, cOp!, true, key));
                }
                else if (destructive is not null)
                {
                    conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Mdc,
                        ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, destructive, cOp!, false, key));
                }
            }

            // A one-sided net Create conflicts with nothing.
        }
    }

    // ------------------------------------------------------------------------
    // Keyed slots: single values, set members, map keys, list items.
    // ------------------------------------------------------------------------

    private static void DetectKeyedSlots(DeltaIndex parent, DeltaIndex child, List<Conflict> conflicts)
    {
        foreach (var (key, cOp) in child.NetKeyed.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!parent.NetKeyed.TryGetValue(key, out var pOp))
            {
                continue;
            }

            // Property changes on an element whose net fate is decided by an
            // element-existence conflict are governed there ("property changes
            // are always applied; the resolution only controls existence").
            if (IsNetDeleted(parent, cOp.ElementId) || IsNetDeleted(child, cOp.ElementId))
            {
                continue;
            }

            var conflict = key[0] switch
            {
                '1' => ClassifySingle(pOp, cOp, key),
                '2' => ClassifySet(pOp, cOp, key),
                '3' => ClassifyMap(pOp, cOp, key),
                '4' => ClassifyListItem(pOp, cOp, key),
                _ => null
            };

            if (conflict is not null)
            {
                conflicts.Add(conflict);
            }
        }
    }

    private static bool IsNetDeleted(DeltaIndex side, string elementId) =>
        side.NetElement.GetValueOrDefault(elementId)?.Type == OperationType.DeleteElement;

    private static Conflict? ClassifySingle(Operation parent, Operation child, string key)
    {
        var parentSets = parent.Type == OperationType.SetProperty;
        var childSets = child.Type == OperationType.SetProperty;

        if (parentSets && childSets)
        {
            var equal = Equals(parent.Value, child.Value);
            return NewConflict(ConflictCategory.SingleValue, MergeConflictType.Mmc,
                equal ? ConflictSeverity.Pseudo : ConflictSeverity.Real,
                equal ? ResolutionPolicy.Ignore : ResolutionPolicy.Choose,
                parent, child, !equal, key);
        }

        if (parentSets != childSets)
        {
            return NewConflict(ConflictCategory.SingleValue,
                parentSets ? MergeConflictType.Mdc : MergeConflictType.Dmc,
                ConflictSeverity.Real, ResolutionPolicy.Choose, parent, child, true, key);
        }

        return NewConflict(ConflictCategory.SingleValue, MergeConflictType.Ddc,
            ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child, false, key);
    }

    private static Conflict? ClassifySet(Operation parent, Operation child, string key)
    {
        var parentAdds = parent.Type == OperationType.AddSetItem;
        var childAdds = child.Type == OperationType.AddSetItem;

        if (parentAdds == childAdds)
        {
            return NewConflict(ConflictCategory.SetMembership,
                parentAdds ? MergeConflictType.Mmc : MergeConflictType.Ddc,
                ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child, false, key);
        }

        // Add vs Remove of the same member: never a true conflict (preconditions
        // are mutually exclusive) but non-commutative, so a resolution operation
        // is required for convergence.
        return NewConflict(ConflictCategory.SetMembership,
            parentAdds ? MergeConflictType.Mdc : MergeConflictType.Dmc,
            ConflictSeverity.Pseudo, ResolutionPolicy.Merge, parent, child, true, key);
    }

    private static Conflict? ClassifyMap(Operation parent, Operation child, string key)
    {
        var parentPuts = parent.Type == OperationType.PutMapEntry;
        var childPuts = child.Type == OperationType.PutMapEntry;

        if (parentPuts && childPuts)
        {
            var equal = Equals(parent.Value, child.Value);
            return NewConflict(ConflictCategory.MapEntry, MergeConflictType.Mmc,
                equal ? ConflictSeverity.Pseudo : ConflictSeverity.Real,
                equal ? ResolutionPolicy.Ignore : ResolutionPolicy.Choose,
                parent, child, !equal, key);
        }

        if (parentPuts != childPuts)
        {
            return NewConflict(ConflictCategory.MapEntry,
                parentPuts ? MergeConflictType.Mdc : MergeConflictType.Dmc,
                ConflictSeverity.Real, ResolutionPolicy.Choose, parent, child, true, key);
        }

        return NewConflict(ConflictCategory.MapEntry, MergeConflictType.Ddc,
            ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child, false, key);
    }

    /// <summary>Both sides netted an operation on the SAME list item.</summary>
    private static Conflict? ClassifyListItem(Operation parent, Operation child, string key)
    {
        var parentInserts = parent.Type == OperationType.InsertListItem;
        var childInserts = child.Type == OperationType.InsertListItem;

        if (parentInserts && childInserts)
        {
            var sameAnchor = string.Equals(parent.AfterItemId, child.AfterItemId, StringComparison.Ordinal);
            var sameValue = Equals(parent.Value, child.Value);
            if (sameAnchor && sameValue)
            {
                return NewConflict(ConflictCategory.ListOrder, MergeConflictType.Mmc,
                    ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child, false, key);
            }

            // Same item placed or valued differently: a binary choice.
            return NewConflict(ConflictCategory.ListOrder, MergeConflictType.Mmc,
                ConflictSeverity.Real, ResolutionPolicy.Choose, parent, child, true, key);
        }

        if (parentInserts != childInserts)
        {
            // Insert/move vs remove of the same item: delete always wins and the
            // pair is commutative under tombstone semantics.
            return NewConflict(ConflictCategory.ListOrder,
                parentInserts ? MergeConflictType.Mdc : MergeConflictType.Dmc,
                ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child, false, key);
        }

        return NewConflict(ConflictCategory.ListOrder, MergeConflictType.Ddc,
            ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child, false, key);
    }

    // ------------------------------------------------------------------------
    // List anchors: competing inserts, deleted anchors, moved anchors.
    // ------------------------------------------------------------------------

    private static void DetectListAnchors(DeltaIndex parent, DeltaIndex child, List<Conflict> conflicts)
    {
        // 1) Competing inserts at the same anchor (different items).
        foreach (var (key, childGroup) in child.AnchorGroups.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!parent.AnchorGroups.TryGetValue(key, out var parentGroup))
            {
                continue;
            }

            var pOp = parentGroup[^1];
            var cOp = childGroup[^1];
            if (string.Equals(pOp.ItemId, cOp.ItemId, StringComparison.Ordinal))
            {
                continue; // same item — handled as a list-item conflict
            }

            if (SkipForNetDeleted(parent, child, cOp.ElementId))
            {
                continue;
            }

            conflicts.Add(NewConflict(ConflictCategory.ListOrder, MergeConflictType.Mmc,
                ConflictSeverity.Real, ResolutionPolicy.Choose, pOp, cOp, true, key));
        }

        // 2) Child inserts whose anchor item the parent net-removed (anchor
        //    deleted) or net-moved (anchor moved): the insert must follow its
        //    anchor's fate. And the mirror for parent inserts.
        DetectAnchorDependencies(parent, child, conflicts, childSide: true);
        DetectAnchorDependencies(child, parent, conflicts, childSide: false);
    }

    /// <summary>
    /// Finds inserts of <paramref name="dependents"/> whose anchor item was
    /// net-removed or net-moved on the <paramref name="anchorOwners"/> side.
    /// </summary>
    private static void DetectAnchorDependencies(
        DeltaIndex anchorOwners,
        DeltaIndex dependents,
        List<Conflict> conflicts,
        bool childSide)
    {
        foreach (var op in dependents.NetKeyed.Values
                     .Where(op => op.Type == OperationType.InsertListItem && op.AfterItemId is not null)
                     .OrderBy(op => op.Id))
        {
            var anchorItemKey = ListItemKey(op.ElementId, op.PropertyName!, op.AfterItemId!);
            if (!anchorOwners.NetKeyed.TryGetValue(anchorItemKey, out var anchorOp))
            {
                continue;
            }

            if (SkipForNetDeleted(anchorOwners, dependents, op.ElementId))
            {
                continue;
            }

            if (anchorOp.Type == OperationType.RemoveListItem)
            {
                conflicts.Add(childSide
                    ? NewConflict(ConflictCategory.ListAnchorDeleted, MergeConflictType.Dmc,
                        ConflictSeverity.Real, ResolutionPolicy.Merge, anchorOp, op, true,
                        AnchorKey(op.ElementId, op.PropertyName!, op.AfterItemId))
                    : NewConflict(ConflictCategory.ListAnchorDeleted, MergeConflictType.Mdc,
                        ConflictSeverity.Real, ResolutionPolicy.Merge, op, anchorOp, true,
                        AnchorKey(op.ElementId, op.PropertyName!, op.AfterItemId)));
            }
            else if (anchorOp.Type == OperationType.InsertListItem)
            {
                // The anchor item itself was moved concurrently; the dependent
                // insert must be re-executed to follow it.
                conflicts.Add(childSide
                    ? NewConflict(ConflictCategory.ListAnchorMoved, MergeConflictType.Mmc,
                        ConflictSeverity.Real, ResolutionPolicy.Merge, anchorOp, op, true,
                        AnchorKey(op.ElementId, op.PropertyName!, op.AfterItemId))
                    : NewConflict(ConflictCategory.ListAnchorMoved, MergeConflictType.Mmc,
                        ConflictSeverity.Real, ResolutionPolicy.Merge, op, anchorOp, true,
                        AnchorKey(op.ElementId, op.PropertyName!, op.AfterItemId)));
            }
        }
    }

    private static bool SkipForNetDeleted(DeltaIndex a, DeltaIndex b, string elementId) =>
        IsNetDeleted(a, elementId) || IsNetDeleted(b, elementId);

    // ------------------------------------------------------------------------

    private static Conflict NewConflict(
        ConflictCategory category,
        MergeConflictType mergeType,
        ConflictSeverity severity,
        ResolutionPolicy policy,
        Operation parent,
        Operation child,
        bool requiresResolution,
        string conflictKey) => new()
    {
        Category = category,
        MergeType = mergeType,
        Severity = severity,
        Policy = policy,
        ParentOperation = parent,
        ChildOperation = child,
        RequiresResolution = requiresResolution,
        ConflictKey = conflictKey
    };

    private static string KeyOf(Operation op) => op.Type switch
    {
        OperationType.SetProperty or OperationType.UnsetProperty =>
            $"1|{op.ElementId}|{op.PropertyName}",
        OperationType.AddSetItem or OperationType.RemoveSetItem =>
            $"2|{op.ElementId}|{op.PropertyName}|{op.Value!.MembershipKey}",
        OperationType.PutMapEntry or OperationType.RemoveMapEntry =>
            $"3|{op.ElementId}|{op.PropertyName}|{op.MapKey}",
        OperationType.InsertListItem or OperationType.RemoveListItem =>
            ListItemKey(op.ElementId, op.PropertyName!, op.ItemId!),
        _ => throw new InvalidOperationException($"No conflict key for {op.Type}.")
    };

    private static string ElementKey(string elementId) => $"element|{elementId}";

    private static string ListItemKey(string elementId, string propertyName, string itemId) =>
        $"4|{elementId}|{propertyName}|{itemId}";

    private static string AnchorKey(string elementId, string propertyName, string? anchor) =>
        $"anchor|{elementId}|{propertyName}|{anchor ?? "<head>"}";
}
