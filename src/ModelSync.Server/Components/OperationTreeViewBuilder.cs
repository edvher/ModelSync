using ModelSync.Core;

namespace ModelSync.Server.Components;

public static class OperationTreeViewBuilder
{
    public static OperationTreeDisplayNode Build(OperationTreeSnapshot snapshot)
    {
        return Build(snapshot, snapshot.RootId);
    }

    private static OperationTreeDisplayNode Build(OperationTreeSnapshot snapshot, Guid nodeId)
    {
        var nodeSnapshot = snapshot.Nodes[nodeId];
        var displayNode = new OperationTreeDisplayNode(nodeSnapshot);

        if (nodeSnapshot.Children.Count == 1)
        {
            var chainIds = CollectChain(snapshot, nodeSnapshot.Children[0]);
            if (chainIds.Count > 3)
            {
                var chainNodes = new List<OperationChainNode>();
                for (var index = 0; index < chainIds.Count; index++)
                {
                    var currentSnapshot = snapshot.Nodes[chainIds[index]];
                    var nextId = index + 1 < chainIds.Count ? chainIds[index + 1] : (Guid?)null;
                    var branchChildren = nextId.HasValue
                        ? currentSnapshot.Children.Where(child => child != nextId.Value)
                        : currentSnapshot.Children;

                    var builtChildren = branchChildren
                        .Select(child => Build(snapshot, child))
                        .ToList();

                    chainNodes.Add(new OperationChainNode(currentSnapshot, builtChildren));
                }

                displayNode.CollapsedChain = new CollapsedChain(chainIds[0], chainNodes);
                return displayNode;
            }
        }

        foreach (var childId in nodeSnapshot.Children)
        {
            displayNode.Children.Add(Build(snapshot, childId));
        }

        return displayNode;
    }

    private static List<Guid> CollectChain(OperationTreeSnapshot snapshot, Guid startingNode)
    {
        var chainIds = new List<Guid>();
        var currentId = startingNode;
        while (snapshot.Nodes.TryGetValue(currentId, out var snapshotNode))
        {
            chainIds.Add(currentId);
            if (snapshotNode.Children.Count != 1)
            {
                break;
            }

            currentId = snapshotNode.Children[0];
        }

        return chainIds;
    }
}
