using ModelSync.Core;

namespace ModelSync.Server.Services;

public class OperationDashboardService
{
    private readonly OperationTree _tree;
    private readonly ModelManager _manager;
    private readonly OperationHub _hub;

    public OperationDashboardService(OperationTree tree, ModelManager manager, OperationHub hub)
    {
        _tree = tree;
        _manager = manager;
        _hub = hub;
    }

    public OperationTreeSnapshot GetSnapshot() => _tree.Snapshot();

    public IAsyncEnumerable<Operation> Subscribe(string modelName, CancellationToken cancellationToken)
    {
        _ = _manager.Checkout(modelName);
        return _hub.Subscribe(modelName, cancellationToken);
    }
}
