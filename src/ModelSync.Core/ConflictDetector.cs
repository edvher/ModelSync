namespace ModelSync.Core;

/// <summary>
/// Incremental, operation-based conflict detection between two divergent
/// operation sequences (the public/parent delta and the private/child delta
/// after their branching point).
///
/// Detection is O(n + m): the parent delta is indexed once by conflict key,
/// then every child operation probes the index. Keys follow the property
/// semantics: (element), (element, property), (element, property, member),
/// (element, property, mapKey), and for lists both the item identity and the
/// insert anchor.
/// </summary>
public static class ConflictDetector
{
    private enum ProbeKind
    {
        Single,
        Set,
        Map,
        /// <summary>Same list item touched on both sides.</summary>
        ListNode,
        /// <summary>Two inserts competing for the same anchor.</summary>
        ListAnchor,
        /// <summary>Child insert whose anchor item the parent removed.</summary>
        ListAnchorRemovedByParent,
        /// <summary>Child removed an item the parent used as insert anchor.</summary>
        ListAnchorRemovedByChild
    }

    public static IReadOnlyList<Conflict> Detect(
        IReadOnlyList<Operation> parentDelta,
        IReadOnlyList<Operation> childDelta)
    {
        // --- index the parent delta -------------------------------------------------
        var lifecycle = new Dictionary<string, Operation>(StringComparer.Ordinal);
        var lastConstructiveTouch = new Dictionary<string, Operation>(StringComparer.Ordinal);
        var lastAnyTouch = new Dictionary<string, Operation>(StringComparer.Ordinal);
        var keyed = new Dictionary<string, Operation>(StringComparer.Ordinal);

        foreach (var op in parentDelta)
        {
            if (op.IsElementOperation)
            {
                lifecycle[op.ElementId] = op;
            }
            else if (op.IsPropertyOperation)
            {
                lastAnyTouch[op.ElementId] = op;
                if (op.IsConstructive)
                {
                    lastConstructiveTouch[op.ElementId] = op;
                }

                foreach (var key in RegistrationKeys(op))
                {
                    keyed[key] = op;
                }
            }
        }

        // --- probe with the child delta ---------------------------------------------
        var conflicts = new List<Conflict>();
        var seenPairs = new HashSet<(Guid, Guid)>();
        var elementExistenceEmitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var child in childDelta)
        {
            switch (child.Type)
            {
                case OperationType.DeleteElement:
                    DetectForChildDelete(child, lifecycle, lastConstructiveTouch, lastAnyTouch, conflicts, seenPairs, elementExistenceEmitted);
                    break;

                case OperationType.CreateElement:
                    DetectForChildCreate(child, lifecycle, conflicts, seenPairs);
                    break;

                default:
                    if (child.IsPropertyOperation)
                    {
                        DetectForChildPropertyOp(child, lifecycle, keyed, conflicts, seenPairs, elementExistenceEmitted);
                    }

                    break;
            }
        }

        return conflicts;
    }

    private static void DetectForChildDelete(
        Operation child,
        Dictionary<string, Operation> lifecycle,
        Dictionary<string, Operation> lastConstructiveTouch,
        Dictionary<string, Operation> lastAnyTouch,
        List<Conflict> conflicts,
        HashSet<(Guid, Guid)> seenPairs,
        HashSet<string> elementExistenceEmitted)
    {
        if (lifecycle.TryGetValue(child.ElementId, out var parentLifecycleOp))
        {
            if (!seenPairs.Add((parentLifecycleOp.Id, child.Id)))
            {
                return;
            }

            if (parentLifecycleOp.Type == OperationType.DeleteElement)
            {
                conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Ddc,
                    ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parentLifecycleOp, child,
                    requiresResolution: false, ElementKey(child.ElementId)));
            }
            else
            {
                // Parent (re)created the element, child deleted it.
                conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Mdc,
                    ConflictSeverity.Real, ResolutionPolicy.Choose, parentLifecycleOp, child,
                    requiresResolution: true, ElementKey(child.ElementId)));
            }

            return;
        }

        if (lastConstructiveTouch.TryGetValue(child.ElementId, out var parentModify))
        {
            if (seenPairs.Add((parentModify.Id, child.Id)))
            {
                conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Mdc,
                    ConflictSeverity.Real, ResolutionPolicy.Choose, parentModify, child,
                    requiresResolution: true, ElementKey(child.ElementId)));
                elementExistenceEmitted.Add(child.ElementId);
            }
        }
        else if (lastAnyTouch.TryGetValue(child.ElementId, out var parentDestructive))
        {
            if (seenPairs.Add((parentDestructive.Id, child.Id)))
            {
                conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Mdc,
                    ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parentDestructive, child,
                    requiresResolution: false, ElementKey(child.ElementId)));
            }
        }
    }

    private static void DetectForChildCreate(
        Operation child,
        Dictionary<string, Operation> lifecycle,
        List<Conflict> conflicts,
        HashSet<(Guid, Guid)> seenPairs)
    {
        if (!lifecycle.TryGetValue(child.ElementId, out var parentLifecycleOp) ||
            !seenPairs.Add((parentLifecycleOp.Id, child.Id)))
        {
            return;
        }

        if (parentLifecycleOp.Type == OperationType.DeleteElement)
        {
            // Parent deleted, child (re)created the same element.
            conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Dmc,
                ConflictSeverity.Real, ResolutionPolicy.Choose, parentLifecycleOp, child,
                requiresResolution: true, ElementKey(child.ElementId)));
        }
        else
        {
            var sameType = string.Equals(parentLifecycleOp.ElementTypeId, child.ElementTypeId, StringComparison.Ordinal);
            conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Mmc,
                sameType ? ConflictSeverity.Pseudo : ConflictSeverity.Real,
                sameType ? ResolutionPolicy.Ignore : ResolutionPolicy.Choose,
                parentLifecycleOp, child,
                requiresResolution: !sameType, ElementKey(child.ElementId)));
        }
    }

    private static void DetectForChildPropertyOp(
        Operation child,
        Dictionary<string, Operation> lifecycle,
        Dictionary<string, Operation> keyed,
        List<Conflict> conflicts,
        HashSet<(Guid, Guid)> seenPairs,
        HashSet<string> elementExistenceEmitted)
    {
        // Parent deleted the element the child is editing.
        if (lifecycle.TryGetValue(child.ElementId, out var parentLifecycleOp) &&
            parentLifecycleOp.Type == OperationType.DeleteElement &&
            !elementExistenceEmitted.Contains(child.ElementId) &&
            seenPairs.Add((parentLifecycleOp.Id, child.Id)))
        {
            var constructive = child.IsConstructive;
            conflicts.Add(NewConflict(ConflictCategory.ElementExistence, MergeConflictType.Dmc,
                constructive ? ConflictSeverity.Real : ConflictSeverity.Pseudo,
                constructive ? ResolutionPolicy.Choose : ResolutionPolicy.Ignore,
                parentLifecycleOp, child,
                requiresResolution: constructive, ElementKey(child.ElementId)));

            if (constructive)
            {
                // Element existence is a binary decision — one conflict per element.
                elementExistenceEmitted.Add(child.ElementId);
            }
        }

        foreach (var (key, kind) in ProbeKeys(child))
        {
            if (!keyed.TryGetValue(key, out var parentOp))
            {
                continue;
            }

            var conflict = ClassifyKeyedPair(parentOp, child, kind, key);
            if (conflict is null)
            {
                continue;
            }

            if (seenPairs.Add((parentOp.Id, child.Id)))
            {
                conflicts.Add(conflict);
            }
        }
    }

    private static IEnumerable<string> RegistrationKeys(Operation op)
    {
        switch (op.Type)
        {
            case OperationType.SetProperty:
            case OperationType.UnsetProperty:
                yield return SingleKey(op);
                break;
            case OperationType.AddSetItem:
            case OperationType.RemoveSetItem:
                yield return SetKey(op);
                break;
            case OperationType.PutMapEntry:
            case OperationType.RemoveMapEntry:
                yield return MapKey(op);
                break;
            case OperationType.InsertListItem:
                yield return ListNodeKey(op, op.ItemId!);
                yield return ListAnchorKey(op, op.AfterItemId);
                break;
            case OperationType.RemoveListItem:
                yield return ListNodeKey(op, op.ItemId!);
                break;
        }
    }

    private static IEnumerable<(string Key, ProbeKind Kind)> ProbeKeys(Operation op)
    {
        switch (op.Type)
        {
            case OperationType.SetProperty:
            case OperationType.UnsetProperty:
                yield return (SingleKey(op), ProbeKind.Single);
                break;
            case OperationType.AddSetItem:
            case OperationType.RemoveSetItem:
                yield return (SetKey(op), ProbeKind.Set);
                break;
            case OperationType.PutMapEntry:
            case OperationType.RemoveMapEntry:
                yield return (MapKey(op), ProbeKind.Map);
                break;
            case OperationType.InsertListItem:
                // Same item touched on the parent side (insert/insert or remove of this item).
                yield return (ListNodeKey(op, op.ItemId!), ProbeKind.ListNode);
                // Competing insert at the same anchor.
                yield return (ListAnchorKey(op, op.AfterItemId), ProbeKind.ListAnchor);
                // The anchor of this insert was removed by the parent.
                if (op.AfterItemId is not null)
                {
                    yield return (ListNodeKey(op, op.AfterItemId), ProbeKind.ListAnchorRemovedByParent);
                }

                break;
            case OperationType.RemoveListItem:
                yield return (ListNodeKey(op, op.ItemId!), ProbeKind.ListNode);
                // The parent inserted something anchored on the item the child removes.
                yield return (ListAnchorKey(op, op.ItemId), ProbeKind.ListAnchorRemovedByChild);
                break;
        }
    }

    private static Conflict? ClassifyKeyedPair(Operation parent, Operation child, ProbeKind kind, string key) => kind switch
    {
        ProbeKind.Single => ClassifySingle(parent, child, key),
        ProbeKind.Set => ClassifySet(parent, child, key),
        ProbeKind.Map => ClassifyMap(parent, child, key),
        ProbeKind.ListNode => ClassifyListNode(parent, child, key),
        ProbeKind.ListAnchor => ClassifyListAnchor(parent, child, key),
        ProbeKind.ListAnchorRemovedByParent => parent.Type == OperationType.RemoveListItem
            ? NewConflict(ConflictCategory.ListAnchorDeleted, MergeConflictType.Dmc,
                ConflictSeverity.Real, ResolutionPolicy.Merge, parent, child,
                requiresResolution: true, key)
            : null,
        ProbeKind.ListAnchorRemovedByChild => parent.Type == OperationType.InsertListItem
            ? NewConflict(ConflictCategory.ListAnchorDeleted, MergeConflictType.Mdc,
                ConflictSeverity.Real, ResolutionPolicy.Merge, parent, child,
                requiresResolution: true, key)
            : null,
        _ => null
    };

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
                parent, child, requiresResolution: !equal, key);
        }

        if (parentSets && !childSets)
        {
            return NewConflict(ConflictCategory.SingleValue, MergeConflictType.Mdc,
                ConflictSeverity.Real, ResolutionPolicy.Choose, parent, child,
                requiresResolution: true, key);
        }

        if (!parentSets && childSets)
        {
            return NewConflict(ConflictCategory.SingleValue, MergeConflictType.Dmc,
                ConflictSeverity.Real, ResolutionPolicy.Choose, parent, child,
                requiresResolution: true, key);
        }

        return NewConflict(ConflictCategory.SingleValue, MergeConflictType.Ddc,
            ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child,
            requiresResolution: false, key);
    }

    private static Conflict? ClassifySet(Operation parent, Operation child, string key)
    {
        var parentAdds = parent.Type == OperationType.AddSetItem;
        var childAdds = child.Type == OperationType.AddSetItem;

        if (parentAdds == childAdds)
        {
            // Add/Add or Remove/Remove of the same member: identical outcome.
            return NewConflict(ConflictCategory.SetMembership,
                parentAdds ? MergeConflictType.Mmc : MergeConflictType.Ddc,
                ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child,
                requiresResolution: false, key);
        }

        // Add vs Remove of the same member: never a true conflict (preconditions are
        // mutually exclusive) but non-commutative, so a resolution operation is
        // required for convergence.
        return NewConflict(ConflictCategory.SetMembership,
            parentAdds ? MergeConflictType.Mdc : MergeConflictType.Dmc,
            ConflictSeverity.Pseudo, ResolutionPolicy.Merge, parent, child,
            requiresResolution: true, key);
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
                parent, child, requiresResolution: !equal, key);
        }

        if (parentPuts != childPuts)
        {
            return NewConflict(ConflictCategory.MapEntry,
                parentPuts ? MergeConflictType.Mdc : MergeConflictType.Dmc,
                ConflictSeverity.Real, ResolutionPolicy.Choose, parent, child,
                requiresResolution: true, key);
        }

        return NewConflict(ConflictCategory.MapEntry, MergeConflictType.Ddc,
            ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child,
            requiresResolution: false, key);
    }

    private static Conflict? ClassifyListNode(Operation parent, Operation child, string key)
    {
        var parentInserts = parent.Type == OperationType.InsertListItem;
        var childInserts = child.Type == OperationType.InsertListItem;

        if (parentInserts && childInserts)
        {
            var sameAnchor = string.Equals(parent.AfterItemId, child.AfterItemId, StringComparison.Ordinal);
            return NewConflict(ConflictCategory.ListOrder, MergeConflictType.Mmc,
                sameAnchor ? ConflictSeverity.Pseudo : ConflictSeverity.Real,
                sameAnchor ? ResolutionPolicy.Ignore : ResolutionPolicy.Choose,
                parent, child, requiresResolution: !sameAnchor, key);
        }

        if (parentInserts != childInserts)
        {
            // Insert/move vs remove of the same item: delete always wins for list
            // items and the pair is commutative under tombstone semantics.
            return NewConflict(ConflictCategory.ListOrder,
                parentInserts ? MergeConflictType.Mdc : MergeConflictType.Dmc,
                ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child,
                requiresResolution: false, key);
        }

        return NewConflict(ConflictCategory.ListOrder, MergeConflictType.Ddc,
            ConflictSeverity.Pseudo, ResolutionPolicy.Ignore, parent, child,
            requiresResolution: false, key);
    }

    private static Conflict? ClassifyListAnchor(Operation parent, Operation child, string key)
    {
        if (parent.Type != OperationType.InsertListItem || child.Type != OperationType.InsertListItem)
        {
            return null;
        }

        if (string.Equals(parent.ItemId, child.ItemId, StringComparison.Ordinal))
        {
            // Identical insert — already covered by the node probe.
            return null;
        }

        // Two different items inserted at the same anchor: a binary ordering choice.
        return NewConflict(ConflictCategory.ListOrder, MergeConflictType.Mmc,
            ConflictSeverity.Real, ResolutionPolicy.Choose, parent, child,
            requiresResolution: true, key);
    }

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

    private static string ElementKey(string elementId) => $"element|{elementId}";
    private static string SingleKey(Operation op) => $"single|{op.ElementId}|{op.PropertyName}";
    private static string SetKey(Operation op) => $"set|{op.ElementId}|{op.PropertyName}|{op.Value!.MembershipKey}";
    private static string MapKey(Operation op) => $"map|{op.ElementId}|{op.PropertyName}|{op.MapKey}";
    private static string ListNodeKey(Operation op, string itemId) => $"listnode|{op.ElementId}|{op.PropertyName}|{itemId}";
    private static string ListAnchorKey(Operation op, string? anchor) => $"listanchor|{op.ElementId}|{op.PropertyName}|{anchor ?? "<head>"}";
}
