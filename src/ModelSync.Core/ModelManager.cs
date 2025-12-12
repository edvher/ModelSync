namespace ModelSync.Core;

public class ModelManager
{
    private readonly OperationTree _tree;
    private readonly Dictionary<string, Model> _models = new(StringComparer.OrdinalIgnoreCase);

    public ModelManager(OperationTree tree)
    {
        _tree = tree;
        _models["P"] = new Model("P", _tree);
    }

    public OperationTree Tree => _tree;

    public Model? GetModel(string name) => _models.TryGetValue(name, out var model) ? model : null;

    public Model Checkout(string modelName)
    {
        var model = GetModel(modelName) ?? CreateModel(modelName);
        var ops = _tree.GetAllOperations(modelName);
        model.ExecuteAll(ops, history: true);
        return model;
    }

    public Model CreateModel(string modelName)
    {
        var model = new Model(modelName, _tree);
        _models[modelName] = model;
        var lastPublic = _tree.GetLastOperation("P");
        if (lastPublic is not null)
        {
            _tree.SetToken(modelName, lastPublic);
        }

        return model;
    }

    public void ApplyOperation(string modelName, Operation operation)
    {
        var model = GetModel(modelName) ?? Checkout(modelName);
        model.Execute(operation, history: false);
    }

    public IReadOnlyList<Operation> Update(string modelName)
    {
        var model = GetModel(modelName) ?? throw new InvalidOperationException($"Model {modelName} not found");
        var branching = _tree.Lca(modelName, "P") ?? throw new InvalidOperationException("No common ancestor");
        var lastPublic = _tree.GetLastOperation("P") ?? branching;
        var lastChild = _tree.GetLastOperation(modelName) ?? branching;

        var publicDelta = _tree.GetPathWithoutFirstOp(branching.Id, lastPublic.Id);
        var childDelta = _tree.GetPathWithoutFirstOp(branching.Id, lastChild.Id);
        var conflicts = ConflictDetection.DetectConflicts(publicDelta, childDelta);
        var resolutionOps = ConflictDetection.ResolveConflicts(conflicts);

        model.ExecuteAll(publicDelta, history: true);
        model.ExecuteAll(resolutionOps, history: false);
        return resolutionOps;
    }

    public IReadOnlyList<Operation> Commit(string modelName)
    {
        var publicModel = GetModel("P") ?? throw new InvalidOperationException("Public model missing");
        var branching = _tree.Lca(modelName, "P") ?? throw new InvalidOperationException("No common ancestor");
        var lastPublic = _tree.GetLastOperation("P") ?? branching;
        var lastChild = _tree.GetLastOperation(modelName) ?? branching;

        var publicDelta = _tree.GetPathWithoutFirstOp(branching.Id, lastPublic.Id);
        if (publicDelta.Any())
        {
            return Array.Empty<Operation>();
        }

        var childDelta = _tree.GetPathWithoutFirstOp(branching.Id, lastChild.Id);
        publicModel.ExecuteAll(childDelta, history: true);
        _tree.SetToken("P", lastChild);
        return childDelta;
    }
}
