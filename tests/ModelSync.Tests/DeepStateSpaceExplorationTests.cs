using ModelSync.Core;
using ModelSync.Exploration;
using Xunit;
using Xunit.Abstractions;

namespace ModelSync.Tests;

/// <summary>
/// Depth-first exploration of the synchronization state space, driven by the
/// reusable explorer in <c>ModelSync.Exploration</c> (the same component that
/// generates the state-space graphs in <c>docs/figures/</c>).
///
/// Topology: one public workspace and two private workspaces (a star; the
/// operation history is a growing tree whose main branch is the public
/// workspace). At every reachable state the explorer enumerates every enabled
/// action — every list insert position, every removal, every single/set/map
/// edit, element delete/recreate (lists bounded to 3 items), plus commit (only
/// when it would fast-forward) and update (both winner strategies when the
/// divergence carries a real conflict). States are deduplicated by canonical
/// signature; the same state is reached through many different action paths.
///
/// The oracle: from EVERY unique state, for BOTH resolution strategies, a full
/// synchronization round (update A, commit A, update B, commit B, update A)
/// must make the public model, both private models and a freshly replayed
/// checkout identical — including soft-deleted elements, tombstone property
/// values and full list chains — while keeping the list invariants intact.
/// </summary>
public class DeepStateSpaceExplorationTests
{
    private const string A = ExplorationScenarios.A;
    private const string B = ExplorationScenarios.B;
    private const string P = ModelService.PublicWorkspaceId;
    private const int MaxListItems = ExplorationScenarios.MaxListItems;

    private readonly ITestOutputHelper _output;
    private int _oracleRuns;

    public DeepStateSpaceExplorationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private ExplorationResult ExploreWithOracle(ExplorationScenario scenario)
    {
        var result = StateSpaceExplorer.Explore(scenario, steps => RunConvergenceOracle(scenario, steps));
        Report(result);
        return result;
    }

    /// <summary>
    /// The full synchronization round from an arbitrary state: everybody
    /// updates (resolving conflicts) and commits in turn. Afterwards all
    /// replicas and a fresh replay must agree on the complete state,
    /// tombstones included.
    /// </summary>
    private void RunConvergenceOracle(ExplorationScenario scenario, IReadOnlyList<ExplorationStep> steps)
    {
        foreach (var strategy in new[] { ResolutionStrategy.ChildWins, ResolutionStrategy.ParentWins })
        {
            var service = scenario.Replay(steps);
            service.Update(A, strategy);
            Assert.True(service.Commit(A).Success);
            service.Update(B, strategy);
            Assert.True(service.Commit(B).Success);
            service.Update(A, strategy);

            var publicModel = service.GetModel(P);
            try
            {
                ModelAssert.EquivalentIncludingTombstones(publicModel, service.GetModel(A));
                ModelAssert.EquivalentIncludingTombstones(publicModel, service.GetModel(B));
                ModelAssert.EquivalentIncludingTombstones(publicModel, service.Checkout("fresh-oracle"));
            }
            catch (Exception ex)
            {
                var scenarioPath = string.Join(" -> ", steps.Select(s => s.Name));
                throw new Xunit.Sdk.XunitException($"Divergence [{scenarioPath}] strategy={strategy}: {ex.Message}");
            }

            AssertListInvariants(publicModel, steps, strategy);
            _oracleRuns++;
        }
    }

    private static void AssertListInvariants(ModelState model, IReadOnlyList<ExplorationStep> steps, ResolutionStrategy strategy)
    {
        foreach (var element in model.Elements)
        {
            foreach (var property in element.Properties.Values.Where(p => p.Cardinality == PropertyCardinality.List))
            {
                var alive = property.ListItems;
                var scenario = string.Join(" -> ", steps.Select(s => s.Name));
                Assert.True(alive.Count <= MaxListItems,
                    $"List bound violated ({alive.Count} items) [{scenario}] strategy={strategy}");
                Assert.Equal(alive.Count, alive.Select(i => i.ItemId).Distinct(StringComparer.Ordinal).Count());
            }
        }
    }

    private void Report(ExplorationResult result)
    {
        var stats = result.Stats;
        _output.WriteLine(
            $"unique states: {stats.UniqueStates}, expanded: {stats.Expanded.Count}, " +
            $"transitions: {result.Graph.Transitions.Count}, total visits: {stats.TotalVisits}, " +
            $"revisited: {stats.RevisitedStates}, in-sync: {stats.InSyncStates}, " +
            $"diverged: {stats.DivergedStates}, conflicted: {stats.ConflictedStates}, oracle runs: {_oracleRuns}");
        _output.WriteLine($"categories: {string.Join(", ", stats.Categories.OrderBy(c => c))}");
        _output.WriteLine($"merge types: {string.Join(", ", stats.MergeTypes.OrderBy(c => c))}");
    }

    /// <summary>
    /// Exhaustive bounded exploration of the LIST state space (the public list
    /// starts empty; A can add two items, B one; lists max 3 items): every
    /// insert position, every removal, every commit/update — including going
    /// through intermediate states repeatedly via different action orders —
    /// must leave the synchronization protocol able to converge everybody.
    /// </summary>
    [Fact]
    public void ListStateSpace_DepthFirstExploration_ConvergesFromEveryReachableState()
    {
        var scenario = ExplorationScenarios.List();
        var result = ExploreWithOracle(scenario);
        var stats = result.Stats;

        // The frontier was exhausted within the depth bound, not cut off.
        Assert.True(stats.Expanded.Count < scenario.MaxExpansions, $"exploration truncated at {stats.Expanded.Count} expansions");

        // Every distinct state passed the oracle for both strategies.
        Assert.Equal(2 * stats.UniqueStates, _oracleRuns);

        // All three synchronization state kinds were reached...
        Assert.True(stats.InSyncStates > 0);
        Assert.True(stats.DivergedStates > 0, "no diverged states reached");
        Assert.True(stats.ConflictedStates > 0, "no conflicted states reached");

        // ... states were re-entered through different action paths ...
        Assert.True(stats.RevisitedStates > 0);

        // ... and the list-specific conflict kinds all occurred.
        Assert.Contains(ConflictCategory.ListOrder, stats.Categories);
        Assert.Contains(ConflictCategory.ListAnchorDeleted, stats.Categories);
        Assert.Contains(ConflictSeverity.Real, stats.Severities);
        Assert.Contains(ConflictSeverity.Pseudo, stats.Severities);
    }

    /// <summary>
    /// Exploration across ALL property kinds at once — single, set, map, list
    /// and the element lifecycle — asserting that every merge-conflict type
    /// (MMC/DMC/MDC/DDC) and every property kind's conflict category shows up
    /// and that every reachable state converges under both strategies.
    /// </summary>
    [Fact]
    public void MixedCardinalityStateSpace_DepthFirstExploration_ConvergesFromEveryReachableState()
    {
        var scenario = ExplorationScenarios.Mixed();
        var result = ExploreWithOracle(scenario);
        var stats = result.Stats;

        Assert.True(stats.Expanded.Count < scenario.MaxExpansions, $"exploration truncated at {stats.Expanded.Count} expansions");
        Assert.Equal(2 * stats.UniqueStates, _oracleRuns);

        Assert.True(stats.InSyncStates > 0);
        Assert.True(stats.DivergedStates > 0);
        Assert.True(stats.ConflictedStates > 0);
        Assert.True(stats.RevisitedStates > 0);

        // Every property kind produced a conflict somewhere in the space.
        Assert.Contains(ConflictCategory.SingleValue, stats.Categories);
        Assert.Contains(ConflictCategory.SetMembership, stats.Categories);
        Assert.Contains(ConflictCategory.MapEntry, stats.Categories);
        Assert.Contains(ConflictCategory.ListOrder, stats.Categories);
        Assert.Contains(ConflictCategory.ElementExistence, stats.Categories);

        // The full MMC/DMC/MDC/DDC classification was exercised.
        Assert.Contains(MergeConflictType.Mmc, stats.MergeTypes);
        Assert.Contains(MergeConflictType.Dmc, stats.MergeTypes);
        Assert.Contains(MergeConflictType.Mdc, stats.MergeTypes);
        Assert.Contains(MergeConflictType.Ddc, stats.MergeTypes);
        Assert.Contains(ConflictSeverity.Real, stats.Severities);
        Assert.Contains(ConflictSeverity.Pseudo, stats.Severities);
    }

    /// <summary>
    /// The paper-sized scenario used for the committed figures: its transition
    /// graph must be small enough to read, structurally sound (every edge
    /// connects known nodes, the initial state is in-sync), convergent from
    /// every state, and exportable to DOT and Mermaid.
    /// </summary>
    [Fact]
    public void PaperListScenario_ProducesConvergentExportableGraph()
    {
        var scenario = ExplorationScenarios.PaperList();
        var result = ExploreWithOracle(scenario);

        Assert.Equal(2 * result.Stats.UniqueStates, _oracleRuns);
        Assert.InRange(result.Graph.Nodes.Count, 10, 120);
        Assert.Equal(StateKind.InSync, result.Graph.InitialNode!.Kind);

        var nodeIds = result.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(result.Graph.Transitions, t =>
        {
            Assert.Contains(t.FromId, nodeIds);
            Assert.Contains(t.ToId, nodeIds);
        });

        var dot = GraphExport.ToDot(result);
        Assert.StartsWith("digraph", dot);
        Assert.Contains(result.Graph.InitialNode!.Id, dot);

        var mermaid = GraphExport.ToMermaid(result);
        Assert.Contains("flowchart TD", mermaid);
        Assert.Contains(result.Graph.InitialNode!.Id, mermaid);
    }
}
