namespace ModelSync.Core;

/// <summary>
/// Conflict awareness: reports the potential conflicts between any two
/// workspaces (public↔private or two private siblings) *before* they
/// synchronize, so engineers can react proactively.
///
/// The result for a pair is derived from the two divergent deltas below the
/// pair's branching point and is cached per (headA, headB); as long as neither
/// workspace moves, queries are O(1), and after a change only the divergent
/// deltas are re-examined.
/// </summary>
public sealed class ConflictAwarenessService
{
    private readonly ModelService _service;
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), CacheEntry> _cache = new();

    private sealed record CacheEntry(Guid HeadA, Guid HeadB, Guid BranchingPoint, IReadOnlyList<Conflict> Conflicts);

    public ConflictAwarenessService(ModelService service)
    {
        _service = service;
    }

    /// <summary>
    /// The conflicts currently brewing between two workspaces. The first
    /// workspace plays the "parent" role of the classification (its operations
    /// appear as <see cref="Conflict.ParentOperation"/>).
    /// </summary>
    public IReadOnlyList<Conflict> GetConflicts(string workspaceA, string workspaceB)
    {
        if (string.Equals(workspaceA, workspaceB, StringComparison.Ordinal))
        {
            return Array.Empty<Conflict>();
        }

        var tree = _service.Tree;
        if (!tree.HasWorkspace(workspaceA) || !tree.HasWorkspace(workspaceB))
        {
            return Array.Empty<Conflict>();
        }

        var headA = tree.Head(workspaceA);
        var headB = tree.Head(workspaceB);

        lock (_gate)
        {
            // An update can re-attach a branch without moving either head, which
            // shifts the branching point — so the LCA is part of the cache key.
            var branchingPoint = tree.Lca(workspaceA, workspaceB);

            var key = (workspaceA, workspaceB);
            if (_cache.TryGetValue(key, out var entry) &&
                entry.HeadA == headA && entry.HeadB == headB && entry.BranchingPoint == branchingPoint)
            {
                return entry.Conflicts;
            }

            var deltaA = tree.PathBetween(branchingPoint, headA);
            var deltaB = tree.PathBetween(branchingPoint, headB);
            var conflicts = ConflictDetector.Detect(deltaA, deltaB);

            _cache[key] = new CacheEntry(headA, headB, branchingPoint, conflicts);
            return conflicts;
        }
    }

    /// <summary>All pairwise conflicts of a workspace against every other workspace.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Conflict>> GetAllConflicts(string workspaceId)
    {
        var result = new Dictionary<string, IReadOnlyList<Conflict>>(StringComparer.Ordinal);
        foreach (var other in _service.Workspaces)
        {
            if (string.Equals(other, workspaceId, StringComparison.Ordinal))
            {
                continue;
            }

            var conflicts = GetConflicts(other, workspaceId);
            if (conflicts.Count > 0)
            {
                result[other] = conflicts;
            }
        }

        return result;
    }
}
