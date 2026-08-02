namespace ModelSync.Core;

/// <summary>
/// The versioned operation history: a tree of operations whose root is a
/// sentinel. Every workspace is a branch, identified by a head pointer to its
/// last operation. Replaying the path from the root to a head reproduces that
/// workspace's model.
///
/// The tree is append-only except for one structural move: when a workspace
/// updates from the public branch, its divergent segment is re-attached onto
/// the public head so histories stay linearizable per branch.
/// </summary>
public sealed class OperationTree
{
    public const string PublicWorkspaceId = "P";

    private sealed class Node
    {
        public required Operation Operation { get; init; }
        public Guid? Parent { get; set; }
        public List<Guid> Children { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Node> _nodes = new();
    private readonly Dictionary<string, Guid> _heads = new(StringComparer.Ordinal);

    public OperationTree()
    {
        var root = new Operation
        {
            Id = Guid.NewGuid(),
            Type = OperationType.Root,
            WorkspaceId = PublicWorkspaceId
        };

        RootId = root.Id;
        _nodes[root.Id] = new Node { Operation = root, Parent = null };
        _heads[PublicWorkspaceId] = root.Id;
    }

    public Guid RootId { get; }

    public IReadOnlyCollection<string> Workspaces
    {
        get
        {
            lock (_gate)
            {
                return _heads.Keys.ToList();
            }
        }
    }

    public bool HasWorkspace(string workspaceId)
    {
        lock (_gate)
        {
            return _heads.ContainsKey(workspaceId);
        }
    }

    /// <summary>Creates the branch for a workspace at the current public head (checkout).</summary>
    public void EnsureBranch(string workspaceId)
    {
        lock (_gate)
        {
            if (!_heads.ContainsKey(workspaceId))
            {
                _heads[workspaceId] = _heads[PublicWorkspaceId];
            }
        }
    }

    public Guid Head(string workspaceId)
    {
        lock (_gate)
        {
            return _heads.TryGetValue(workspaceId, out var head)
                ? head
                : throw new InvalidOperationException($"Unknown workspace '{workspaceId}'.");
        }
    }

    public Operation HeadOperation(string workspaceId)
    {
        lock (_gate)
        {
            return _nodes[Head(workspaceId)].Operation;
        }
    }

    public void SetHead(string workspaceId, Guid operationId)
    {
        lock (_gate)
        {
            if (!_nodes.ContainsKey(operationId))
            {
                throw new InvalidOperationException($"Operation {operationId} is not part of the tree.");
            }

            _heads[workspaceId] = operationId;
        }
    }

    /// <summary>Appends an operation to a workspace branch and advances its head.</summary>
    public void Append(string workspaceId, Operation operation)
    {
        lock (_gate)
        {
            EnsureBranchUnlocked(workspaceId);
            var parentId = _heads[workspaceId];
            var node = new Node { Operation = operation, Parent = parentId };
            _nodes[operation.Id] = node;
            _nodes[parentId].Children.Add(operation.Id);
            _heads[workspaceId] = operation.Id;
        }
    }

    /// <summary>The operations from the root to the workspace head, in execution order.</summary>
    public IReadOnlyList<Operation> PathFromRoot(string workspaceId)
    {
        lock (_gate)
        {
            var path = AncestryUnlocked(Head(workspaceId));
            path.Reverse();
            return path.Select(id => _nodes[id].Operation).Where(op => op.Type != OperationType.Root).ToList();
        }
    }

    /// <summary>
    /// The operations strictly after <paramref name="fromExclusive"/> up to and
    /// including <paramref name="toInclusive"/>, in execution order.
    /// </summary>
    public IReadOnlyList<Operation> PathBetween(Guid fromExclusive, Guid toInclusive)
    {
        lock (_gate)
        {
            var path = new List<Operation>();
            var current = toInclusive;
            while (current != fromExclusive)
            {
                if (!_nodes.TryGetValue(current, out var node))
                {
                    throw new InvalidOperationException($"Operation {current} is not part of the tree.");
                }

                if (node.Parent is null)
                {
                    // Reached the root without passing fromExclusive.
                    if (current != fromExclusive)
                    {
                        throw new InvalidOperationException("The target operation is not a descendant of the start operation.");
                    }

                    break;
                }

                path.Add(node.Operation);
                current = node.Parent.Value;
            }

            path.Reverse();
            return path;
        }
    }

    /// <summary>The lowest common ancestor (branching point) of two workspace heads.</summary>
    public Guid Lca(string workspaceA, string workspaceB)
    {
        lock (_gate)
        {
            var ancestors = new HashSet<Guid>(AncestryUnlocked(Head(workspaceA)));
            var current = Head(workspaceB);
            while (true)
            {
                if (ancestors.Contains(current))
                {
                    return current;
                }

                var node = _nodes[current];
                if (node.Parent is null)
                {
                    return RootId;
                }

                current = node.Parent.Value;
            }
        }
    }

    /// <summary>
    /// Re-attaches the subtree rooted at <paramref name="operationId"/> below a
    /// new parent. Used by update to move a workspace's divergent segment on
    /// top of the public head after the public delta was replayed into it.
    /// </summary>
    public void Reattach(Guid operationId, Guid newParentId)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(operationId, out var node))
            {
                throw new InvalidOperationException($"Operation {operationId} is not part of the tree.");
            }

            if (!_nodes.ContainsKey(newParentId))
            {
                throw new InvalidOperationException($"Operation {newParentId} is not part of the tree.");
            }

            // Guard against cycles: the new parent must not be a descendant of the moved node.
            var cursor = newParentId;
            while (true)
            {
                if (cursor == operationId)
                {
                    throw new InvalidOperationException("Re-attaching would create a cycle in the operation tree.");
                }

                var parent = _nodes[cursor].Parent;
                if (parent is null)
                {
                    break;
                }

                cursor = parent.Value;
            }

            if (node.Parent is { } oldParent)
            {
                _nodes[oldParent].Children.Remove(operationId);
            }

            node.Parent = newParentId;
            _nodes[newParentId].Children.Add(operationId);
        }
    }

    public OperationTreeSnapshot Snapshot()
    {
        lock (_gate)
        {
            var nodes = _nodes.ToDictionary(
                pair => pair.Key,
                pair => new OperationTreeSnapshotNode(pair.Key, pair.Value.Operation, pair.Value.Parent, pair.Value.Children.ToList()));
            var heads = _heads.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return new OperationTreeSnapshot(RootId, nodes, heads);
        }
    }

    private void EnsureBranchUnlocked(string workspaceId)
    {
        if (!_heads.ContainsKey(workspaceId))
        {
            _heads[workspaceId] = _heads[PublicWorkspaceId];
        }
    }

    private List<Guid> AncestryUnlocked(Guid start)
    {
        var result = new List<Guid>();
        var current = start;
        while (true)
        {
            result.Add(current);
            var node = _nodes[current];
            if (node.Parent is null)
            {
                return result;
            }

            current = node.Parent.Value;
        }
    }
}

public sealed record OperationTreeSnapshotNode(Guid Id, Operation Operation, Guid? Parent, IReadOnlyList<Guid> Children);

public sealed record OperationTreeSnapshot(
    Guid RootId,
    IReadOnlyDictionary<Guid, OperationTreeSnapshotNode> Nodes,
    IReadOnlyDictionary<string, Guid> Heads);
