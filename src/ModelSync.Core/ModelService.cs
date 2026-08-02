namespace ModelSync.Core;

/// <summary>Result of updating a private workspace from the public branch.</summary>
public sealed record UpdateResult(
    bool WasUpToDate,
    IReadOnlyList<Operation> PublicOperations,
    IReadOnlyList<Conflict> Conflicts,
    IReadOnlyList<Operation> ResolutionOperations)
{
    public static UpdateResult UpToDate { get; } =
        new(true, Array.Empty<Operation>(), Array.Empty<Conflict>(), Array.Empty<Operation>());
}

/// <summary>Result of committing a private workspace to the public branch.</summary>
public sealed record CommitResult(
    bool Success,
    string? Reason,
    IReadOnlyList<Operation> CommittedOperations)
{
    public static CommitResult NotUpToDate { get; } =
        new(false, "The workspace is behind the public branch; update first.", Array.Empty<Operation>());
}

/// <summary>
/// The central synchronization mechanism. All operations of all workspaces run
/// through this service, which guarantees a consistent global order, maintains
/// the operation tree (one branch per workspace) and the materialized model
/// per workspace, and performs update/commit with incremental conflict
/// detection and deterministic resolution.
/// </summary>
public sealed class ModelService
{
    public const string PublicWorkspaceId = OperationTree.PublicWorkspaceId;

    private readonly object _gate = new();
    private readonly Dictionary<string, ModelState> _models = new(StringComparer.Ordinal);

    public ModelService()
    {
        Tree = new OperationTree();
        _models[PublicWorkspaceId] = new ModelState(PublicWorkspaceId);
    }

    public OperationTree Tree { get; }

    /// <summary>Raised after operations were applied to a workspace branch (user ops, update, commit).</summary>
    public event Action<string, IReadOnlyList<Operation>>? OperationsApplied;

    /// <summary>Raised after a workspace synchronized (update or commit) with the public branch.</summary>
    public event Action<string>? WorkspaceSynchronized;

    public IReadOnlyCollection<string> Workspaces => Tree.Workspaces;

    /// <summary>
    /// Creates the workspace branch at the current public head if needed and
    /// returns the workspace's materialized model (rebuilt by replaying its
    /// branch when it does not exist yet).
    /// </summary>
    public ModelState Checkout(string workspaceId)
    {
        ValidateWorkspaceId(workspaceId);
        lock (_gate)
        {
            Tree.EnsureBranch(workspaceId);
            if (!_models.TryGetValue(workspaceId, out var model))
            {
                model = new ModelState(workspaceId);
                model.ApplyAll(Tree.PathFromRoot(workspaceId));
                _models[workspaceId] = model;
            }

            return model;
        }
    }

    public ModelState GetModel(string workspaceId)
    {
        lock (_gate)
        {
            return _models.TryGetValue(workspaceId, out var model)
                ? model
                : throw new InvalidOperationException($"Workspace '{workspaceId}' has not been checked out.");
        }
    }

    /// <summary>The full operation history of a workspace branch (for replay by clients).</summary>
    public IReadOnlyList<Operation> History(string workspaceId)
    {
        lock (_gate)
        {
            Tree.EnsureBranch(workspaceId);
            return Tree.PathFromRoot(workspaceId);
        }
    }

    /// <summary>
    /// Applies a new user operation to a workspace: validates it against the
    /// current model, executes it and appends it to the workspace branch.
    /// </summary>
    public Operation Apply(string workspaceId, Operation operation)
    {
        ValidateWorkspaceId(workspaceId);
        List<Operation> applied;
        lock (_gate)
        {
            var model = Checkout(workspaceId);
            Validate(model, operation);
            model.Apply(operation);
            Tree.Append(workspaceId, operation);
            applied = new List<Operation> { operation };
        }

        OperationsApplied?.Invoke(workspaceId, applied);
        return operation;
    }

    /// <summary>
    /// Pulls the public changes into a private workspace: computes both deltas
    /// from the branching point (LCA), detects conflicts between them, applies
    /// the public delta, re-attaches the private branch onto the public head
    /// and appends deterministic resolution operations for every
    /// non-commutative conflict.
    /// </summary>
    public UpdateResult Update(string workspaceId, ResolutionStrategy strategy = ResolutionStrategy.ChildWins)
    {
        ValidateWorkspaceId(workspaceId);
        if (workspaceId == PublicWorkspaceId)
        {
            throw new InvalidOperationException("The public workspace cannot update from itself.");
        }

        UpdateResult result;
        lock (_gate)
        {
            var model = Checkout(workspaceId);
            var branchingPoint = Tree.Lca(workspaceId, PublicWorkspaceId);
            var publicHead = Tree.Head(PublicWorkspaceId);
            var childHead = Tree.Head(workspaceId);

            var publicDelta = Tree.PathBetween(branchingPoint, publicHead);
            if (publicDelta.Count == 0)
            {
                return UpdateResult.UpToDate;
            }

            var childDelta = Tree.PathBetween(branchingPoint, childHead);
            var conflicts = ConflictDetector.Detect(publicDelta, childDelta);

            // 1) Replay the public changes onto the private model. These operations
            //    already exist in the tree; only the branch shape changes.
            model.ApplyAll(publicDelta);

            // 2) Re-attach the private branch onto the public head so the branch
            //    path replays as: public history, then private delta.
            if (childDelta.Count > 0)
            {
                Tree.Reattach(childDelta[0].Id, publicHead);
            }
            else
            {
                Tree.SetHead(workspaceId, publicHead);
            }

            // 3) Resolve: append one resolution operation per non-commutative
            //    conflict; re-execution makes every replica converge.
            var resolvedConflicts = new List<Conflict>(conflicts.Count);
            var resolutionOps = new List<Operation>();
            foreach (var conflict in conflicts)
            {
                var resolutions = ConflictResolver.CreateResolutions(conflict, strategy, model, workspaceId, publicDelta, childDelta);
                foreach (var resolution in resolutions)
                {
                    model.Apply(resolution);
                    Tree.Append(workspaceId, resolution);
                    resolutionOps.Add(resolution);
                }

                resolvedConflicts.Add(resolutions.Count > 0 ? conflict with { Resolution = resolutions[0] } : conflict);
            }

            result = new UpdateResult(false, publicDelta, resolvedConflicts, resolutionOps);
        }

        var notified = new List<Operation>(result.PublicOperations);
        notified.AddRange(result.ResolutionOperations);
        OperationsApplied?.Invoke(workspaceId, notified);
        WorkspaceSynchronized?.Invoke(workspaceId);
        return result;
    }

    /// <summary>
    /// Pushes the private workspace's changes to the public branch. Requires
    /// the workspace to be up to date (fast-forward only): the public branch
    /// replays the private delta and its head moves to the private head.
    /// </summary>
    public CommitResult Commit(string workspaceId)
    {
        ValidateWorkspaceId(workspaceId);
        if (workspaceId == PublicWorkspaceId)
        {
            throw new InvalidOperationException("The public workspace cannot commit to itself.");
        }

        CommitResult result;
        lock (_gate)
        {
            Checkout(workspaceId);
            var branchingPoint = Tree.Lca(workspaceId, PublicWorkspaceId);
            var publicHead = Tree.Head(PublicWorkspaceId);
            var childHead = Tree.Head(workspaceId);

            var publicDelta = Tree.PathBetween(branchingPoint, publicHead);
            if (publicDelta.Count > 0)
            {
                return CommitResult.NotUpToDate;
            }

            var childDelta = Tree.PathBetween(branchingPoint, childHead);
            if (childDelta.Count == 0)
            {
                return new CommitResult(true, null, Array.Empty<Operation>());
            }

            var publicModel = _models[PublicWorkspaceId];
            publicModel.ApplyAll(childDelta);
            Tree.SetHead(PublicWorkspaceId, childHead);
            result = new CommitResult(true, null, childDelta);
        }

        OperationsApplied?.Invoke(PublicWorkspaceId, result.CommittedOperations);
        WorkspaceSynchronized?.Invoke(workspaceId);
        return result;
    }

    /// <summary>
    /// Validates a new user operation against the workspace's current model.
    /// History replay is never validated — this guards the public API only.
    /// </summary>
    private static void Validate(ModelState model, Operation operation)
    {
        if (operation.Id == Guid.Empty)
        {
            throw new ArgumentException("Operation id must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(operation.ElementId))
        {
            throw new ArgumentException("Operation element id must not be empty.");
        }

        if (operation.Type == OperationType.Root)
        {
            throw new ArgumentException("Root is not an applicable operation.");
        }

        if (operation.Type == OperationType.CreateElement)
        {
            return; // Creating (or resurrecting) is always allowed.
        }

        var element = model.GetElement(operation.ElementId);
        if (element is null)
        {
            throw new InvalidOperationException(
                $"Element '{operation.ElementId}' does not exist (or was deleted); new operations on it are not allowed.");
        }

        if (operation.IsPropertyOperation && string.IsNullOrWhiteSpace(operation.PropertyName))
        {
            throw new ArgumentException($"{operation.Type} requires a property name.");
        }

        switch (operation.Type)
        {
            case OperationType.InsertListItem:
            {
                if (string.IsNullOrWhiteSpace(operation.ItemId))
                {
                    throw new ArgumentException("InsertListItem requires an item id.");
                }

                if (operation.AfterItemId is not null)
                {
                    var property = element.GetProperty(operation.PropertyName!);
                    var anchor = property?.FindNode(operation.AfterItemId);
                    if (anchor is null || anchor.IsDeleted)
                    {
                        throw new InvalidOperationException(
                            $"Anchor item '{operation.AfterItemId}' does not exist in {operation.ElementId}.{operation.PropertyName}.");
                    }
                }

                break;
            }

            case OperationType.RemoveListItem:
            {
                if (string.IsNullOrWhiteSpace(operation.ItemId))
                {
                    throw new ArgumentException("RemoveListItem requires an item id.");
                }

                var property = element.GetProperty(operation.PropertyName!);
                var node = property?.FindNode(operation.ItemId);
                if (node is null || node.IsDeleted)
                {
                    throw new InvalidOperationException(
                        $"List item '{operation.ItemId}' does not exist in {operation.ElementId}.{operation.PropertyName}.");
                }

                break;
            }

            case OperationType.PutMapEntry or OperationType.RemoveMapEntry when string.IsNullOrWhiteSpace(operation.MapKey):
                throw new ArgumentException($"{operation.Type} requires a map key.");
        }
    }

    private static void ValidateWorkspaceId(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("Workspace id must not be empty.");
        }
    }
}
