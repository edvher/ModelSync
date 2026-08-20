using System.Text;
using ModelSync.Core;

namespace ModelSync.Exploration;

/// <summary>One explorable action: a stable name plus a pure-data replayable effect.</summary>
public sealed record ExplorationStep(string Name, Action<ModelService> Run);

/// <summary>Synchronization classification of a reachable state.</summary>
public enum StateKind
{
    /// <summary>All private branches sit on the public head; nothing pending.</summary>
    InSync,

    /// <summary>At least one branch needs a commit or update to merge (no conflicts).</summary>
    Diverged,

    /// <summary>Some divergence carries conflicts: a resolution is required to rejoin.</summary>
    Conflicted
}

public sealed record StateNode(string Id, string Label, StateKind Kind);

public sealed record StateTransition(string FromId, string ActionName, string ToId);

/// <summary>The explored state space as a graph: unique states and the actions between them.</summary>
public sealed class StateSpaceGraph
{
    private readonly Dictionary<string, StateNode> _bySignature = new(StringComparer.Ordinal);
    private readonly List<StateNode> _nodes = new();
    private readonly List<StateTransition> _transitions = new();
    private readonly HashSet<string> _transitionKeys = new(StringComparer.Ordinal);

    public IReadOnlyList<StateNode> Nodes => _nodes;
    public IReadOnlyList<StateTransition> Transitions => _transitions;

    /// <summary>The base state every exploration starts from ("S0").</summary>
    public StateNode? InitialNode => _nodes.Count > 0 ? _nodes[0] : null;

    internal StateNode GetOrAdd(string signature, string label, StateKind kind)
    {
        if (_bySignature.TryGetValue(signature, out var node))
        {
            return node;
        }

        node = new StateNode($"S{_nodes.Count}", label, kind);
        _bySignature[signature] = node;
        _nodes.Add(node);
        return node;
    }

    internal StateNode NodeOf(string signature) => _bySignature[signature];

    internal void AddTransition(string fromId, string actionName, string toId)
    {
        if (_transitionKeys.Add($"{fromId}|{actionName}|{toId}"))
        {
            _transitions.Add(new StateTransition(fromId, actionName, toId));
        }
    }
}

/// <summary>Aggregate statistics of one exploration run.</summary>
public sealed class ExplorationStats
{
    /// <summary>Visit count per unique state signature (&gt; 1 = reached via multiple paths).</summary>
    public Dictionary<string, int> Visits { get; } = new(StringComparer.Ordinal);

    /// <summary>Signatures whose outgoing actions were expanded (each unique state once).</summary>
    public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);

    public int InSyncStates { get; internal set; }
    public int DivergedStates { get; internal set; }
    public int ConflictedStates { get; internal set; }

    /// <summary>Conflict coverage observed while classifying states.</summary>
    public HashSet<ConflictCategory> Categories { get; } = new();
    public HashSet<MergeConflictType> MergeTypes { get; } = new();
    public HashSet<ConflictSeverity> Severities { get; } = new();

    public int UniqueStates => Visits.Count;
    public int TotalVisits => Visits.Values.Sum();
    public int RevisitedStates => Visits.Values.Count(c => c > 1);
}

/// <summary>
/// A bounded exploration configuration over the star topology: a seeded base
/// model, the private workspaces, and a generator that enumerates every
/// enabled action in a given state.
/// </summary>
public sealed class ExplorationScenario
{
    public required string Name { get; init; }

    /// <summary>Human description used in exports and reports.</summary>
    public required string Description { get; init; }

    /// <summary>Applies the base operations through the first private workspace.</summary>
    public required Action<ModelService> Seed { get; init; }

    /// <summary>Enumerates every enabled action in the service's current state.</summary>
    public required Func<ModelService, IEnumerable<ExplorationStep>> StepsOf { get; init; }

    /// <summary>Compact human label of the current state (used as graph node label).</summary>
    public required Func<ModelService, string> Label { get; init; }

    public required IReadOnlyList<string> PrivateWorkspaces { get; init; }

    public int MaxDepth { get; init; } = 5;
    public int MaxExpansions { get; init; } = 25_000;

    /// <summary>
    /// Rebuilds a service from scratch: seed the base through the first private
    /// workspace, publish it, check out the other workspaces, then replay the
    /// action path. Deterministic — a node of the search tree is fully
    /// identified by its action path.
    /// </summary>
    public ModelService Replay(IReadOnlyList<ExplorationStep> steps)
    {
        var service = new ModelService();
        var seeder = PrivateWorkspaces[0];
        service.Checkout(seeder);
        Seed(service);
        var commit = service.Commit(seeder);
        if (!commit.Success)
        {
            throw new InvalidOperationException($"Seeding commit failed: {commit.Reason}");
        }

        foreach (var ws in PrivateWorkspaces.Skip(1))
        {
            service.Checkout(ws);
            service.Update(ws);
        }

        foreach (var step in steps)
        {
            step.Run(service);
        }

        return service;
    }
}

public sealed class ExplorationResult
{
    public required ExplorationScenario Scenario { get; init; }
    public required StateSpaceGraph Graph { get; init; }
    public required ExplorationStats Stats { get; init; }
}

/// <summary>
/// Depth-first exploration of the synchronization state space. States are
/// deduplicated by a canonical signature of all models plus branch divergence;
/// each unique state is expanded once but can be reached through many action
/// paths. Records the full transition graph for reporting and paper figures.
/// </summary>
public static class StateSpaceExplorer
{
    /// <param name="onFirstVisit">
    /// Invoked once per unique state with the action path that first reached
    /// it — the hook where a test oracle replays the path and asserts
    /// convergence invariants.
    /// </param>
    public static ExplorationResult Explore(
        ExplorationScenario scenario,
        Action<IReadOnlyList<ExplorationStep>>? onFirstVisit = null)
    {
        var stats = new ExplorationStats();
        var graph = new StateSpaceGraph();
        Visit(scenario, new List<ExplorationStep>(), stats, graph, onFirstVisit);
        return new ExplorationResult { Scenario = scenario, Graph = graph, Stats = stats };
    }

    private static StateNode Visit(
        ExplorationScenario scenario,
        List<ExplorationStep> prefix,
        ExplorationStats stats,
        StateSpaceGraph graph,
        Action<IReadOnlyList<ExplorationStep>>? onFirstVisit)
    {
        var service = scenario.Replay(prefix);
        var signature = Signature(service, scenario);
        var firstVisit = !stats.Visits.TryGetValue(signature, out var visits);
        stats.Visits[signature] = visits + 1;

        StateNode node;
        if (firstVisit)
        {
            var kind = Classify(service, scenario, stats);
            node = graph.GetOrAdd(signature, scenario.Label(service), kind);
            onFirstVisit?.Invoke(prefix);
        }
        else
        {
            node = graph.NodeOf(signature);
        }

        if (prefix.Count >= scenario.MaxDepth || !firstVisit || stats.Expanded.Count >= scenario.MaxExpansions)
        {
            return node;
        }

        stats.Expanded.Add(signature);
        foreach (var step in scenario.StepsOf(service).ToList())
        {
            prefix.Add(step);
            var child = Visit(scenario, prefix, stats, graph, onFirstVisit);
            prefix.RemoveAt(prefix.Count - 1);
            graph.AddTransition(node.Id, step.Name, child.Id);
        }

        return node;
    }

    /// <summary>
    /// In-sync: every branch sits on the public head with nothing pending.
    /// Diverged: some branch needs a commit or update. Conflicted: some
    /// divergence carries conflicts, i.e. a resolution is required to rejoin.
    /// </summary>
    private static StateKind Classify(ModelService service, ExplorationScenario scenario, ExplorationStats stats)
    {
        var anyDiverged = false;
        var anyConflict = false;
        foreach (var ws in scenario.PrivateWorkspaces)
        {
            var lca = service.Tree.Lca(ws, ModelService.PublicWorkspaceId);
            var publicDelta = service.Tree.PathBetween(lca, service.Tree.Head(ModelService.PublicWorkspaceId));
            var childDelta = service.Tree.PathBetween(lca, service.Tree.Head(ws));
            anyDiverged |= publicDelta.Count > 0 || childDelta.Count > 0;

            if (publicDelta.Count > 0 && childDelta.Count > 0)
            {
                var conflicts = ConflictDetector.Detect(publicDelta, childDelta);
                anyConflict |= conflicts.Count > 0;
                foreach (var conflict in conflicts)
                {
                    stats.Categories.Add(conflict.Category);
                    stats.MergeTypes.Add(conflict.MergeType);
                    stats.Severities.Add(conflict.Severity);
                }
            }
        }

        if (anyConflict)
        {
            stats.ConflictedStates++;
            return StateKind.Conflicted;
        }

        if (anyDiverged)
        {
            stats.DivergedStates++;
            return StateKind.Diverged;
        }

        stats.InSyncStates++;
        return StateKind.InSync;
    }

    /// <summary>
    /// Canonical signature of the whole synchronization state: the public and
    /// all private models (including tombstones — they anchor future inserts)
    /// plus each private branch's ahead/behind divergence.
    /// </summary>
    public static string Signature(ModelService service, ExplorationScenario scenario)
    {
        var sb = new StringBuilder();
        sb.Append("P{");
        AppendModel(sb, service.GetModel(ModelService.PublicWorkspaceId));
        sb.Append('}');
        foreach (var ws in scenario.PrivateWorkspaces)
        {
            sb.Append(ws).Append('{');
            AppendModel(sb, service.GetModel(ws));
            sb.Append('}');
        }

        foreach (var ws in scenario.PrivateWorkspaces)
        {
            var lca = service.Tree.Lca(ws, ModelService.PublicWorkspaceId);
            sb.Append(ws)
                .Append('+').Append(service.Tree.PathBetween(lca, service.Tree.Head(ws)).Count)
                .Append('-').Append(service.Tree.PathBetween(lca, service.Tree.Head(ModelService.PublicWorkspaceId)).Count);
        }

        return sb.ToString();
    }

    private static void AppendModel(StringBuilder sb, ModelState model)
    {
        foreach (var element in model.AllElements.Values.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            sb.Append(element.Id).Append('|').Append(element.TypeId).Append('|').Append(element.IsAlive ? '+' : '-');
            foreach (var property in element.Properties.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                sb.Append(';').Append(property.Name).Append(':');
                switch (property.Cardinality)
                {
                    case PropertyCardinality.Single:
                        sb.Append(property.SingleValue?.Content ?? "∅");
                        break;
                    case PropertyCardinality.UnorderedSet:
                        sb.AppendJoin(',', property.SetValues.Select(v => v.MembershipKey).OrderBy(v => v, StringComparer.Ordinal));
                        break;
                    case PropertyCardinality.Map:
                        sb.AppendJoin(',', property.MapValues.OrderBy(p => p.Key, StringComparer.Ordinal)
                            .Select(p => $"{p.Key}={p.Value.Content}"));
                        break;
                    case PropertyCardinality.List:
                        sb.AppendJoin(',', property.ListNodes.Select(n => $"{n.ItemId}={n.Value.Content}{(n.IsDeleted ? "†" : "")}"));
                        break;
                }
            }

            sb.Append('/');
        }
    }
}
