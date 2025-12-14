using ModelSync.Core;

namespace ModelSync.Server.Components;

public class OperationTreeDisplayNode
{
    public OperationTreeDisplayNode(OperationNodeSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public OperationNodeSnapshot Snapshot { get; }
    public List<OperationTreeDisplayNode> Children { get; } = new();
    public CollapsedChain? CollapsedChain { get; set; }
}

public record CollapsedChain(Guid Key, IReadOnlyList<OperationChainNode> Nodes);

public record OperationChainNode(OperationNodeSnapshot Snapshot, IReadOnlyList<OperationTreeDisplayNode> Children);

public record ChainExpansion(Guid ChainKey, int ChainLength);
