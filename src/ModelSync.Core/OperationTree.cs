namespace ModelSync.Core;

public class OperationTree
{
    private readonly Dictionary<Guid, OperationNode> _nodes = new();
    private readonly Dictionary<string, Guid> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public OperationTree()
    {
        var root = new Operation(Guid.NewGuid(), OperationType.None) { ModelName = "P" };
        var node = new OperationNode(root, null);
        RootId = root.Id;
        _nodes[root.Id] = node;
        _tokens["P"] = root.Id;
    }

    public Guid RootId { get; }

    public Operation? GetLastOperation(string branchId)
    {
        return _tokens.TryGetValue(branchId, out var opId) && _nodes.TryGetValue(opId, out var node)
            ? node.Operation
            : null;
    }

    public void SetToken(string branchId, Operation op)
    {
        _tokens[branchId] = op.Id;
    }

    public void AddOperationToModel(string modelName, Operation operation)
    {
        var parent = _tokens.TryGetValue(modelName, out var parentId) ? parentId : RootId;
        var node = new OperationNode(operation, parent);
        _nodes[operation.Id] = node;

        if (_nodes.TryGetValue(parent, out var parentNode))
        {
            parentNode.Children.Add(operation.Id);
        }

        _tokens[modelName] = operation.Id;
    }

    public IReadOnlyList<Operation> GetAllOperations(string modelName)
    {
        if (!_tokens.TryGetValue(modelName, out var targetId))
        {
            return Array.Empty<Operation>();
        }

        var path = BuildPath(targetId);
        path.Reverse();
        return path.Select(id => _nodes[id].Operation).ToList();
    }

    public IReadOnlyList<Operation> GetPathWithoutFirstOp(Guid first, Guid last)
    {
        var path = new List<Guid>();
        var current = last;
        while (current != first && _nodes.TryGetValue(current, out var node))
        {
            path.Add(current);
            if (node.Parent is null)
            {
                break;
            }
            current = node.Parent.Value;
        }

        path.Reverse();
        return path.Select(id => _nodes[id].Operation).ToList();
    }

    public Operation? GetOperation(Guid id) => _nodes.TryGetValue(id, out var node) ? node.Operation : null;

    public Operation? Lca(string tokenId1, string tokenId2)
    {
        if (!_tokens.TryGetValue(tokenId1, out var left) || !_tokens.TryGetValue(tokenId2, out var right))
        {
            return null;
        }

        var ancestors = new HashSet<Guid>();
        var current = left;
        while (_nodes.TryGetValue(current, out var node))
        {
            ancestors.Add(current);
            if (node.Parent is null)
            {
                break;
            }
            current = node.Parent.Value;
        }

        current = right;
        while (_nodes.TryGetValue(current, out var node))
        {
            if (ancestors.Contains(current))
            {
                return node.Operation;
            }

            if (node.Parent is null)
            {
                break;
            }

            current = node.Parent.Value;
        }

        return null;
    }

    public OperationTreeSnapshot Snapshot()
    {
        var nodes = _nodes.ToDictionary(
            pair => pair.Key,
            pair => new OperationNodeSnapshot(
                pair.Key,
                pair.Value.Operation,
                pair.Value.Parent,
                pair.Value.Children.ToList()));

        return new OperationTreeSnapshot(RootId, nodes);
    }

    private List<Guid> BuildPath(Guid target)
    {
        var result = new List<Guid>();
        var current = target;
        while (_nodes.TryGetValue(current, out var node))
        {
            result.Add(current);
            if (node.Parent is null)
            {
                break;
            }
            current = node.Parent.Value;
        }

        return result;
    }
}
