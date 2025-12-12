namespace ModelSync.Core;

public record Conflict(Operation LeftOp, Operation RightOp, ConflictType ConflictType)
{
    public Operation? Resolution { get; set; }
}

public static class ConflictDetection
{
    public static List<Conflict> DetectConflicts(IEnumerable<Operation> operations1, IEnumerable<Operation> operations2)
    {
        var ops1 = operations1.ToList();
        var ops2 = operations2.ToList();
        var shorter = ops1.Count <= ops2.Count ? ops1 : ops2;
        var longer = ReferenceEquals(shorter, ops1) ? ops2 : ops1;

        var map = new Dictionary<(string elementId, string property), Operation>();
        foreach (var op in shorter)
        {
            if (string.IsNullOrWhiteSpace(op.PropertyName))
            {
                continue;
            }

            map[(op.ElementId, op.PropertyName!)] = op;
        }

        var conflicts = new List<Conflict>();
        foreach (var op in longer)
        {
            if (string.IsNullOrWhiteSpace(op.PropertyName))
            {
                continue;
            }

            if (map.TryGetValue((op.ElementId, op.PropertyName!), out var op1))
            {
                var conflictType = ClassifyConflict(op1, op);
                if (conflictType != ConflictType.None)
                {
                    conflicts.Add(new Conflict(op1, op, conflictType));
                }
            }
        }

        return conflicts;
    }

    public static List<Operation> ResolveConflicts(IEnumerable<Conflict> conflicts)
    {
        var resolutions = new List<Operation>();
        foreach (var conflict in conflicts)
        {
            var winner = ChooseWinner(conflict);
            if (winner is null)
            {
                continue;
            }

            var resolved = Clone(winner);
            if (conflict.ConflictType == ConflictType.ElementDelete && winner.Type != OperationType.DeleteElement)
            {
                resolved = InvertDelete(conflict.LeftOp.Type == OperationType.DeleteElement ? conflict.LeftOp : conflict.RightOp);
            }

            conflict.Resolution = resolved;
            resolutions.Add(resolved);
        }

        return resolutions;
    }

    private static Operation Clone(Operation op)
    {
        return new Operation(Guid.NewGuid(), op.Type)
        {
            ModelName = op.ModelName,
            ElementId = op.ElementId,
            ElementType = op.ElementType,
            PropertyName = op.PropertyName,
            NewValue = op.NewValue,
            AfterItemId = op.AfterItemId,
            ItemId = op.ItemId,
            MapKey = op.MapKey
        };
    }

    private static Operation InvertDelete(Operation deleteOp)
    {
        return new Operation(Guid.NewGuid(), OperationType.CreateElement)
        {
            ModelName = deleteOp.ModelName,
            ElementId = deleteOp.ElementId,
            ElementType = deleteOp.ElementType
        };
    }

    private static Operation? ChooseWinner(Conflict conflict) => conflict.RightOp;

    private static ConflictType ClassifyConflict(Operation op1, Operation op2)
    {
        if (op1.ElementId != op2.ElementId)
        {
            return ConflictType.None;
        }

        if (op1.Type == OperationType.DeleteElement || op2.Type == OperationType.DeleteElement)
        {
            return ConflictType.ElementDelete;
        }

        if (!string.Equals(op1.PropertyName, op2.PropertyName, StringComparison.OrdinalIgnoreCase))
        {
            return ConflictType.None;
        }

        return ConflictType.PropertyWrite;
    }
}
