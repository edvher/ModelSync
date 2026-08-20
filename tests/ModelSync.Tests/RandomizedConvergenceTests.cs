using ModelSync.Core;
using ModelSync.Exploration;
using Xunit;

namespace ModelSync.Tests;

/// <summary>
/// Property-based randomized testing: the probabilistic complement to the
/// exhaustive-but-bounded depth-first exploration. Three private workspaces
/// issue random operations over every property kind and randomly interleave
/// updates (with random winner strategies) and commits; after a final full
/// synchronization round, all replicas and a fresh replay must agree on the
/// complete state — soft-deleted elements, tombstone values and full list
/// chains included.
///
/// Seeds are fixed, so every run is reproducible; on failure the executed
/// action script is printed for diagnosis.
/// </summary>
public class RandomizedConvergenceTests
{
    private static readonly string[] Workspaces = { "A", "B", "C" };
    private const string ElementId = ExplorationScenarios.ElementId;

    private static ModelService NewSeededService()
    {
        var service = new ModelService();
        service.Checkout("A");
        service.Apply("A", Op.Create(ElementId));
        service.Apply("A", Op.Set(ElementId, "s", "v0"));
        service.Apply("A", Op.AddSet(ElementId, "set", "m0"));
        service.Apply("A", Op.Put(ElementId, "map", "k0", "v0"));
        Assert.True(service.Commit("A").Success);
        service.Checkout("B");
        service.Update("B");
        service.Checkout("C");
        service.Update("C");
        return service;
    }

    /// <summary>
    /// All actions enabled in the current state, across all three workspaces:
    /// the mixed-cardinality edit generator of the exploration library (one
    /// own list item per workspace keeps merged lists within the bound of 3)
    /// plus the enabled sync actions.
    /// </summary>
    private static List<ExplorationStep> EnabledSteps(ModelService service)
    {
        var steps = new List<ExplorationStep>();
        foreach (var ws in Workspaces)
        {
            steps.AddRange(ExplorationScenarios.MixedEditSteps(service, ws, new[] { ws.ToLowerInvariant() + "-L" }));
        }

        steps.AddRange(ExplorationScenarios.SyncSteps(service, Workspaces));
        return steps;
    }

    private static ResolutionStrategy RandomStrategy(Random rng) =>
        rng.Next(2) == 0 ? ResolutionStrategy.ChildWins : ResolutionStrategy.ParentWins;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void RandomOperationSequences_AlwaysConvergeAfterFullSyncRound(int seed)
    {
        const int iterations = 25;
        const int stepsPerIteration = 30;
        var rng = new Random(seed);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var service = NewSeededService();
            var script = new List<string>();

            try
            {
                for (var i = 0; i < stepsPerIteration; i++)
                {
                    var steps = EnabledSteps(service);
                    if (steps.Count == 0)
                    {
                        break; // Everything synchronized and no edit enabled — extremely unlikely.
                    }

                    var step = steps[rng.Next(steps.Count)];
                    script.Add(step.Name);
                    step.Run(service);
                }

                // Final full synchronization round: everybody updates
                // (resolving with a random strategy) and commits in turn, then
                // the stragglers pull once more.
                foreach (var ws in Workspaces)
                {
                    var strategy = RandomStrategy(rng);
                    script.Add($"{ws}:final-update:{strategy}");
                    service.Update(ws, strategy);
                    script.Add($"{ws}:final-commit");
                    Assert.True(service.Commit(ws).Success);
                }

                foreach (var ws in Workspaces)
                {
                    service.Update(ws);
                }

                var publicModel = service.GetModel(ModelService.PublicWorkspaceId);
                foreach (var ws in Workspaces)
                {
                    ModelAssert.EquivalentIncludingTombstones(publicModel, service.GetModel(ws));
                }

                ModelAssert.EquivalentIncludingTombstones(
                    publicModel, service.Checkout("fresh-" + Guid.NewGuid().ToString("N")));

                AssertListBound(publicModel);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Randomized divergence (seed={seed}, iteration={iteration}):\n" +
                    $"script: {string.Join(" -> ", script)}\n{ex.Message}");
            }
        }
    }

    private static void AssertListBound(ModelState model)
    {
        foreach (var element in model.Elements)
        {
            foreach (var property in element.Properties.Values.Where(p => p.Cardinality == PropertyCardinality.List))
            {
                var alive = property.ListItems;
                Assert.True(alive.Count <= ExplorationScenarios.MaxListItems,
                    $"List bound violated: {alive.Count} items");
                Assert.Equal(alive.Count, alive.Select(i => i.ItemId).Distinct(StringComparer.Ordinal).Count());
            }
        }
    }
}
