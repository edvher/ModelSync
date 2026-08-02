namespace ModelSync.Core;

/// <summary>REAL conflicts require a decision; PSEUDO conflicts converge on their own.</summary>
public enum ConflictSeverity
{
    Real,
    Pseudo
}

/// <summary>The classic merge taxonomy over the two sides (parent = public, child = private).</summary>
public enum MergeConflictType
{
    /// <summary>Both sides modify — ModifyModifyConflict.</summary>
    Mmc,
    /// <summary>Parent deletes, child modifies — DeleteModifyConflict.</summary>
    Dmc,
    /// <summary>Parent modifies, child deletes — ModifyDeleteConflict.</summary>
    Mdc,
    /// <summary>Both sides delete — DeleteDeleteConflict.</summary>
    Ddc
}

/// <summary>What conflicted, in terms of the property model.</summary>
public enum ConflictCategory
{
    /// <summary>Concurrent writes to a single-valued property.</summary>
    SingleValue,
    /// <summary>Element delete vs. concurrent element operations.</summary>
    ElementExistence,
    /// <summary>Concurrent membership changes of the same set value.</summary>
    SetMembership,
    /// <summary>Concurrent changes of the same map key.</summary>
    MapEntry,
    /// <summary>Concurrent list inserts competing for the same position, or contradictory positions for the same item.</summary>
    ListOrder,
    /// <summary>A list insert whose anchor item was deleted concurrently.</summary>
    ListAnchorDeleted,
    /// <summary>A list insert whose anchor item was moved concurrently; the insert must follow it.</summary>
    ListAnchorMoved
}

/// <summary>The manual-triage policy associated with a conflict.</summary>
public enum ResolutionPolicy
{
    /// <summary>Pseudo conflict — outcomes agree, nothing to decide.</summary>
    Ignore,
    /// <summary>Outcomes contradict — exactly one side can win.</summary>
    Choose,
    /// <summary>Both sides can be combined, possibly with adaptation (e.g. re-anchoring).</summary>
    Merge
}

/// <summary>Deterministic strategies for automatic conflict resolution.</summary>
public enum ResolutionStrategy
{
    /// <summary>The updating (private) workspace wins.</summary>
    ChildWins,
    /// <summary>The public/parent workspace wins.</summary>
    ParentWins
}

/// <summary>
/// A detected conflict between one operation of the parent (public) delta and
/// one operation of the child (private) delta.
/// </summary>
public sealed record Conflict
{
    public required ConflictCategory Category { get; init; }
    public required MergeConflictType MergeType { get; init; }
    public required ConflictSeverity Severity { get; init; }
    public required ResolutionPolicy Policy { get; init; }

    /// <summary>The conflicting operation from the public (parent) delta.</summary>
    public required Operation ParentOperation { get; init; }

    /// <summary>The conflicting operation from the private (child) delta.</summary>
    public required Operation ChildOperation { get; init; }

    /// <summary>
    /// True when the pair is non-commutative, i.e. a resolution operation must
    /// be appended so that all replicas converge to the same state.
    /// </summary>
    public required bool RequiresResolution { get; init; }

    /// <summary>The key on which the two operations collided.</summary>
    public required string ConflictKey { get; init; }

    /// <summary>The resolution operation chosen for this conflict, if any.</summary>
    public Operation? Resolution { get; init; }

    public override string ToString() =>
        $"{Severity} {MergeType} [{Category}] on {ConflictKey}: parent({ParentOperation}) vs child({ChildOperation})";
}
