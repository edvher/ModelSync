using ModelSync.Core;

namespace ModelSync.Server.Services;

public class OperationDashboardService
{
    private readonly OperationTree _tree;
    private readonly ModelManager _manager;
    private readonly OperationHub _hub;
    private bool _demoInitialized;

    public OperationDashboardService(OperationTree tree, ModelManager manager, OperationHub hub)
    {
        _tree = tree;
        _manager = manager;
        _hub = hub;
    }

    public OperationTreeSnapshot GetSnapshot()
    {
        EnsureDemoData();
        return _tree.Snapshot();
    }

    public IAsyncEnumerable<Operation> Subscribe(string modelName, CancellationToken cancellationToken)
    {
        _ = _manager.Checkout(modelName);
        return _hub.Subscribe(modelName, cancellationToken);
    }

    private void EnsureDemoData()
    {
        if (_demoInitialized || _tree.Snapshot().Nodes.Count > 1)
        {
            return;
        }

        _demoInitialized = true;

        var now = DateTimeOffset.UtcNow;

        var mainChain = new[]
        {
            BuildOperation(Core.OperationType.CreateElement, "P", "diagram", "Diagram", "name", "System Diagram", now.AddMinutes(-30)),
            BuildOperation(Core.OperationType.CreateElement, "P", "component-a", "Component", "name", "Component A", now.AddMinutes(-27)),
            BuildOperation(Core.OperationType.SetProperty, "P", "component-a", null, "status", "Initialized", now.AddMinutes(-24)),
            BuildOperation(Core.OperationType.AddToListProperty, "P", "component-a", null, "ports", "Add input port", now.AddMinutes(-21)),
            BuildOperation(Core.OperationType.AddToListProperty, "P", "component-a", null, "ports", "Add output port", now.AddMinutes(-18)),
            BuildOperation(Core.OperationType.SetProperty, "P", "component-a", null, "status", "Connected", now.AddMinutes(-15)),
            BuildOperation(Core.OperationType.UpdateMapEntry, "P", "component-a", null, "metadata", "Add author", now.AddMinutes(-12)),
        };

        foreach (var op in mainChain)
        {
            _tree.AddOperationToModel(op.ModelName, op);
        }

        _tree.SetToken("Feature-X", mainChain[2]);

        var featureBranch = new[]
        {
            BuildOperation(Core.OperationType.SetProperty, "Feature-X", "component-a", null, "status", "Feature X draft", now.AddMinutes(-10)),
            BuildOperation(Core.OperationType.AddToSetProperty, "Feature-X", "component-a", null, "tags", "Telemetry", now.AddMinutes(-8)),
            BuildOperation(Core.OperationType.SetProperty, "Feature-X", "component-a", null, "status", "Feature X ready", now.AddMinutes(-6)),
        };

        foreach (var op in featureBranch)
        {
            _tree.AddOperationToModel(op.ModelName, op);
        }

        _tree.SetToken("Exploration", mainChain[1]);

        var exploration = new[]
        {
            BuildOperation(Core.OperationType.CreateElement, "Exploration", "component-b", "Component", "name", "Component B", now.AddMinutes(-14)),
            BuildOperation(Core.OperationType.SetProperty, "Exploration", "component-b", null, "status", "Prototype", now.AddMinutes(-11)),
            BuildOperation(Core.OperationType.AddToListProperty, "Exploration", "component-b", null, "ports", "Diagnostics", now.AddMinutes(-9)),
        };

        foreach (var op in exploration)
        {
            _tree.AddOperationToModel(op.ModelName, op);
        }

        _tree.SetToken("Exploration-Branch", exploration[1]);

        var nested = new[]
        {
            BuildOperation(Core.OperationType.AddToSetProperty, "Exploration-Branch", "component-b", null, "tags", "Discovery", now.AddMinutes(-7)),
            BuildOperation(Core.OperationType.RemoveFromSetProperty, "Exploration-Branch", "component-b", null, "tags", "Remove legacy", now.AddMinutes(-5)),
        };

        foreach (var op in nested)
        {
            _tree.AddOperationToModel(op.ModelName, op);
        }
    }

    private static Operation BuildOperation(
        Core.OperationType type,
        string modelName,
        string elementId,
        string? elementType,
        string? propertyName,
        string? value,
        DateTimeOffset timestamp)
    {
        return new Operation(Guid.NewGuid(), type)
        {
            ModelName = modelName,
            ElementId = elementId,
            ElementType = elementType,
            PropertyName = propertyName,
            NewValue = value is null ? null : PropertyValue.FromString(value),
            Timestamp = timestamp,
        };
    }
}
