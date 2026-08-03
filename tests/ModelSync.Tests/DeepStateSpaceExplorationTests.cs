using System.Text;
using ModelSync.Core;
using Xunit;

namespace ModelSync.Tests;

/// <summary>
/// Depth-first exploration of the synchronization state space.
///
/// Topology: one public workspace and two private workspaces (a star; the
/// operation history is a growing tree whose main branch is the public
/// workspace and whose other branches are the private workspaces). From the
/// base state the explorer enumerates, at every reachable state, every enabled
/// action:
///  - edit actions per private workspace derived from its CURRENT model
///    (set/unset singles, add/remove set members, put/remove map entries,
///    insert a list item at every possible position, remove every list item,
///    delete/recreate the element) — lists are bounded to 3 items;
///  - sync actions: update (with either winner strategy when the divergence
///    carries real conflicts) and commit (only when it would fast-forward).
///
/// States are deduplicated by a canonical signature of all three models plus
/// the branch divergence, so the traversal expands each distinct state once
/// but can (and does) reach the same state through many different action
/// paths. Every state is classified as in-sync, diverged (a commit/update is
/// required to merge) or conflicted (a resolution is required before the
/// workspaces can rejoin).
///
/// The oracle: from EVERY visited state, for BOTH resolution strategies, a
/// full synchronization round (update A, commit A, update B, commit B,
/// update A) must make the public model, both private models and a freshly
/// replayed checkout identical, while keeping the list invariants intact.
/// </summary>
public class DeepStateSpaceExplorationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public DeepStateSpaceExplorationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private void Report(ExplorationStats stats)
    {
        _output.WriteLine(
            $"unique states: {stats.Visits.Count}, expanded: {stats.Expanded.Count}, " +
            $"total visits: {stats.Visits.Values.Sum()}, revisited: {stats.Visits.Values.Count(c => c > 1)}, " +
            $"in-sync: {stats.InSyncStates}, diverged: {stats.DivergedStates}, conflicted: {stats.ConflictedStates}, " +
            $"oracle runs: {stats.OracleRuns}");
        _output.WriteLine($"categories: {string.Join(", ", stats.Categories.OrderBy(c => c))}");
        _output.WriteLine($"merge types: {string.Join(", ", stats.MergeTypes.OrderBy(c => c))}");
    }

    private const string A = "A";
    private const string B = "B";
    private const string P = ModelService.PublicWorkspaceId;
    private const string Element = "e";
    private const string ListProperty = "list";
    private const int MaxListItems = 3;

    /// <summary>One explorable action: a stable name plus a pure-data replayable effect.</summary>
    private sealed record Step(string Name, Action<ModelService> Run);

    private sealed class ExplorationStats
    {
        public Dictionary<string, int> Visits { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Expanded { get; } = new(StringComparer.Ordinal);
        public int InSyncStates;
        public int DivergedStates;
        public int ConflictedStates;
        public int OracleRuns;
        public HashSet<ConflictCategory> Categories { get; } = new();
        public HashSet<MergeConflictType> MergeTypes { get; } = new();
        public HashSet<ConflictSeverity> Severities { get; } = new();
    }

    // ------------------------------------------------------------ exploration

    /// <summary>
    /// Rebuilds the service from scratch: seed the base through A, publish it,
    /// check out B, then replay the chosen action path. Deterministic, so a
    /// node of the search tree is fully identified by its action path.
    /// </summary>
    private static ModelService Replay(Action<ModelService> seed, IReadOnlyList<Step> steps)
    {
        var service = new ModelService();
        service.Checkout(A);
        seed(service);
        Assert.True(service.Commit(A).Success);
        service.Checkout(B);
        service.Update(B);
        foreach (var step in steps)
        {
            step.Run(service);
        }

        return service;
    }

    private static void Explore(
        Action<ModelService> seed,
        Func<ModelService, IEnumerable<Step>> stepsOf,
        List<Step> prefix,
        ExplorationStats stats,
        int maxDepth,
        int maxExpansions)
    {
        var service = Replay(seed, prefix);
        var signature = Signature(service);
        var firstVisit = !stats.Visits.TryGetValue(signature, out var visits);
        stats.Visits[signature] = visits + 1;

        if (firstVisit)
        {
            Classify(service, stats);
            RunConvergenceOracle(seed, prefix, stats);
        }

        if (prefix.Count >= maxDepth || !firstVisit || stats.Expanded.Count >= maxExpansions)
        {
            return;
        }

        stats.Expanded.Add(signature);
        foreach (var step in stepsOf(service).ToList())
        {
            prefix.Add(step);
            Explore(seed, stepsOf, prefix, stats, maxDepth, maxExpansions);
            prefix.RemoveAt(prefix.Count - 1);
        }
    }

    /// <summary>
    /// The full synchronization round from an arbitrary state: everybody
    /// updates (resolving conflicts) and commits in turn. Afterwards all
    /// replicas and a fresh replay of the public branch must be identical.
    /// </summary>
    private static void RunConvergenceOracle(
        Action<ModelService> seed,
        IReadOnlyList<Step> steps,
        ExplorationStats stats)
    {
        foreach (var strategy in new[] { ResolutionStrategy.ChildWins, ResolutionStrategy.ParentWins })
        {
            var service = Replay(seed, steps);
            service.Update(A, strategy);
            Assert.True(service.Commit(A).Success);
            service.Update(B, strategy);
            Assert.True(service.Commit(B).Success);
            service.Update(A, strategy);

            var publicModel = service.GetModel(P);
            try
            {
                ModelAssert.Equivalent(publicModel, service.GetModel(A));
                ModelAssert.Equivalent(publicModel, service.GetModel(B));
                ModelAssert.Equivalent(publicModel, service.Checkout("fresh-oracle"));
            }
            catch (Exception ex)
            {
                var scenario = string.Join(" -> ", steps.Select(s => s.Name));
                throw new Xunit.Sdk.XunitException($"Divergence [{scenario}] strategy={strategy}: {ex.Message}");
            }

            AssertListInvariants(publicModel, steps, strategy);
            stats.OracleRuns++;
        }
    }

    private static void AssertListInvariants(ModelState model, IReadOnlyList<Step> steps, ResolutionStrategy strategy)
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

    /// <summary>
    /// In-sync: both branches sit on the public head with nothing pending.
    /// Diverged: at least one branch needs a commit or update to merge.
    /// Conflicted: some divergence carries conflicts, i.e. a resolution (a
    /// winner choice) is required before that workspace can rejoin.
    /// </summary>
    private static void Classify(ModelService service, ExplorationStats stats)
    {
        var anyDiverged = false;
        var anyConflict = false;
        foreach (var ws in new[] { A, B })
        {
            var lca = service.Tree.Lca(ws, P);
            var publicDelta = service.Tree.PathBetween(lca, service.Tree.Head(P));
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
        }
        else if (anyDiverged)
        {
            stats.DivergedStates++;
        }
        else
        {
            stats.InSyncStates++;
        }
    }

    // -------------------------------------------------------------- signature

    /// <summary>
    /// Canonical signature of the whole synchronization state: the public and
    /// both private models (including tombstones — they anchor future inserts)
    /// plus each private branch's ahead/behind divergence.
    /// </summary>
    private static string Signature(ModelService service)
    {
        var sb = new StringBuilder();
        foreach (var ws in new[] { P, A, B })
        {
            sb.Append(ws).Append('{');
            AppendModel(sb, service.GetModel(ws));
            sb.Append('}');
        }

        foreach (var ws in new[] { A, B })
        {
            var lca = service.Tree.Lca(ws, P);
            sb.Append(ws)
                .Append("+").Append(service.Tree.PathBetween(lca, service.Tree.Head(ws)).Count)
                .Append("-").Append(service.Tree.PathBetween(lca, service.Tree.Head(P)).Count);
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

    // -------------------------------------------------------- action generators

    /// <summary>
    /// The sync actions enabled in the current state. Update is offered with
    /// both strategies only when the divergence contains a conflict requiring
    /// a winner choice; commit only when it would actually fast-forward.
    /// </summary>
    private static IEnumerable<Step> SyncSteps(ModelService service)
    {
        foreach (var ws in new[] { A, B })
        {
            var lca = service.Tree.Lca(ws, P);
            var publicDelta = service.Tree.PathBetween(lca, service.Tree.Head(P));
            var childDelta = service.Tree.PathBetween(lca, service.Tree.Head(ws));
            var w = ws;

            if (publicDelta.Count > 0)
            {
                if (ConflictDetector.Detect(publicDelta, childDelta).Any(c => c.RequiresResolution))
                {
                    yield return new Step($"{w}:update:child-wins", s => s.Update(w, ResolutionStrategy.ChildWins));
                    yield return new Step($"{w}:update:parent-wins", s => s.Update(w, ResolutionStrategy.ParentWins));
                }
                else
                {
                    yield return new Step($"{w}:update", s => s.Update(w));
                }
            }
            else if (childDelta.Count > 0)
            {
                yield return new Step($"{w}:commit", s => Assert.True(s.Commit(w).Success));
            }
        }
    }

    /// <summary>
    /// Every list edit possible in the workspace's current view: insert the
    /// next unused own item at the head and after every alive item (while the
    /// list holds fewer than 3), and remove every alive item.
    /// </summary>
    private static IEnumerable<Step> ListEditSteps(ModelService service, string ws, string[] pool)
    {
        var element = service.GetModel(ws).GetElement(Element);
        if (element is null)
        {
            yield break;
        }

        var property = element.GetProperty(ListProperty);
        var alive = property?.ListItems ?? Array.Empty<ListNode>();

        var next = pool.FirstOrDefault(id => property?.FindNode(id) is null);
        if (next is not null && alive.Count < MaxListItems)
        {
            yield return new Step($"{ws}:ins:{next}@head",
                s => s.Apply(ws, Op.Insert(Element, ListProperty, next, next, null)));
            foreach (var anchor in alive)
            {
                var anchorId = anchor.ItemId;
                yield return new Step($"{ws}:ins:{next}@{anchorId}",
                    s => s.Apply(ws, Op.Insert(Element, ListProperty, next, next, anchorId)));
            }
        }

        foreach (var item in alive)
        {
            var itemId = item.ItemId;
            yield return new Step($"{ws}:rm:{itemId}",
                s => s.Apply(ws, Op.RemoveItem(Element, ListProperty, itemId)));
        }
    }

    /// <summary>
    /// Edits over every property kind, from the workspace's current view:
    /// singles, a shared set member, a shared map key, the bounded list and the
    /// element lifecycle (delete alive / recreate deleted).
    /// </summary>
    private static IEnumerable<Step> MixedEditSteps(ModelService service, string ws, string[] listPool)
    {
        var model = service.GetModel(ws);
        var element = model.GetElement(Element);
        if (element is null)
        {
            if (model.GetElementIncludingDeleted(Element) is not null)
            {
                yield return new Step($"{ws}:create", s => s.Apply(ws, Op.Create(Element)));
            }

            yield break;
        }

        yield return new Step($"{ws}:delete", s => s.Apply(ws, Op.Delete(Element)));

        // Single value.
        var ownValue = $"{ws}-v";
        if (element.GetProperty("s")?.SingleValue?.Content != ownValue)
        {
            yield return new Step($"{ws}:set", s => s.Apply(ws, Op.Set(Element, "s", ownValue)));
        }

        if (element.GetProperty("s")?.SingleValue is not null)
        {
            yield return new Step($"{ws}:unset", s => s.Apply(ws, Op.Unset(Element, "s")));
        }

        // Set membership over a shared member pool.
        var set = element.GetProperty("set");
        if (set?.ContainsSetValue(PropertyValue.String("m1")) != true)
        {
            yield return new Step($"{ws}:add:m1", s => s.Apply(ws, Op.AddSet(Element, "set", "m1")));
        }

        foreach (var member in (set?.SetValues ?? Array.Empty<PropertyValue>()).Select(v => v.Content).OrderBy(v => v, StringComparer.Ordinal))
        {
            var m = member;
            yield return new Step($"{ws}:rm-set:{m}", s => s.Apply(ws, Op.RemoveSet(Element, "set", m)));
        }

        // Map entries on a shared key.
        var map = element.GetProperty("map");
        if (map?.MapValues.GetValueOrDefault("k0")?.Content != ownValue)
        {
            yield return new Step($"{ws}:put:k0", s => s.Apply(ws, Op.Put(Element, "map", "k0", ownValue)));
        }

        foreach (var key in (map?.MapValues.Keys ?? Enumerable.Empty<string>()).OrderBy(k => k, StringComparer.Ordinal))
        {
            var k = key;
            yield return new Step($"{ws}:rm-map:{k}", s => s.Apply(ws, Op.RemoveMap(Element, "map", k)));
        }

        foreach (var step in ListEditSteps(service, ws, listPool))
        {
            yield return step;
        }
    }

    // ------------------------------------------------------------------ tests

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
        var poolA = new[] { "a1", "a2" };
        var poolB = new[] { "b1" };
        Action<ModelService> seed = s => s.Apply(A, Op.Create(Element));
        Func<ModelService, IEnumerable<Step>> stepsOf = s =>
            ListEditSteps(s, A, poolA)
                .Concat(ListEditSteps(s, B, poolB))
                .Concat(SyncSteps(s));

        var stats = new ExplorationStats();
        Explore(seed, stepsOf, new List<Step>(), stats, maxDepth: 6, maxExpansions: 25_000);
        Report(stats);

        // The frontier was exhausted within the depth bound, not cut off.
        Assert.True(stats.Expanded.Count < 25_000, $"exploration truncated at {stats.Expanded.Count} expansions");

        // Every distinct state passed the oracle for both strategies.
        Assert.Equal(2 * stats.Visits.Count, stats.OracleRuns);

        // All three synchronization state kinds were reached...
        Assert.True(stats.InSyncStates > 0);
        Assert.True(stats.DivergedStates > 0, "no diverged states reached");
        Assert.True(stats.ConflictedStates > 0, "no conflicted states reached");

        // ... states were re-entered through different action paths ...
        Assert.Contains(stats.Visits.Values, count => count > 1);

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
        var poolA = new[] { "a-L" };
        var poolB = new[] { "b-L" };
        Action<ModelService> seed = s =>
        {
            s.Apply(A, Op.Create(Element));
            s.Apply(A, Op.Set(Element, "s", "v0"));
            s.Apply(A, Op.AddSet(Element, "set", "m0"));
            s.Apply(A, Op.Put(Element, "map", "k0", "v0"));
            s.Apply(A, Op.Insert(Element, ListProperty, "L0", "L0", null));
        };
        Func<ModelService, IEnumerable<Step>> stepsOf = s =>
            MixedEditSteps(s, A, poolA)
                .Concat(MixedEditSteps(s, B, poolB))
                .Concat(SyncSteps(s));

        var stats = new ExplorationStats();
        Explore(seed, stepsOf, new List<Step>(), stats, maxDepth: 3, maxExpansions: 25_000);
        Report(stats);

        Assert.True(stats.Expanded.Count < 25_000, $"exploration truncated at {stats.Expanded.Count} expansions");
        Assert.Equal(2 * stats.Visits.Count, stats.OracleRuns);

        Assert.True(stats.InSyncStates > 0);
        Assert.True(stats.DivergedStates > 0);
        Assert.True(stats.ConflictedStates > 0);
        Assert.Contains(stats.Visits.Values, count => count > 1);

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
}
