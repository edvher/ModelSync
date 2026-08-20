using System.Text;
using ModelSync.Core;

namespace ModelSync.Exploration;

/// <summary>Operation factory for the built-in scenario generators.</summary>
internal static class Ops
{
    public static Operation Create(string elementId) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.CreateElement,
        WorkspaceId = "",
        ElementId = elementId
    };

    public static Operation Delete(string elementId) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.DeleteElement,
        WorkspaceId = "",
        ElementId = elementId
    };

    public static Operation Set(string elementId, string property, string value) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.SetProperty,
        WorkspaceId = "",
        ElementId = elementId,
        PropertyName = property,
        Value = PropertyValue.String(value)
    };

    public static Operation Unset(string elementId, string property) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.UnsetProperty,
        WorkspaceId = "",
        ElementId = elementId,
        PropertyName = property
    };

    public static Operation AddSet(string elementId, string property, string value) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.AddSetItem,
        WorkspaceId = "",
        ElementId = elementId,
        PropertyName = property,
        Value = PropertyValue.String(value)
    };

    public static Operation RemoveSet(string elementId, string property, string value) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.RemoveSetItem,
        WorkspaceId = "",
        ElementId = elementId,
        PropertyName = property,
        Value = PropertyValue.String(value)
    };

    public static Operation Put(string elementId, string property, string key, string value) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.PutMapEntry,
        WorkspaceId = "",
        ElementId = elementId,
        PropertyName = property,
        MapKey = key,
        Value = PropertyValue.String(value)
    };

    public static Operation RemoveMap(string elementId, string property, string key) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.RemoveMapEntry,
        WorkspaceId = "",
        ElementId = elementId,
        PropertyName = property,
        MapKey = key
    };

    public static Operation Insert(string elementId, string property, string itemId, string value, string? after) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.InsertListItem,
        WorkspaceId = "",
        ElementId = elementId,
        PropertyName = property,
        ItemId = itemId,
        AfterItemId = after,
        Value = PropertyValue.String(value)
    };

    public static Operation RemoveItem(string elementId, string property, string itemId) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.RemoveListItem,
        WorkspaceId = "",
        ElementId = elementId,
        PropertyName = property,
        ItemId = itemId
    };
}

/// <summary>
/// The built-in exploration scenarios and their action generators. All of them
/// use one shared element with bounded lists (max 3 items) and two private
/// workspaces "A" and "B" around the public workspace. A move needs no action
/// of its own: it is represented by a remove followed by an insert.
/// </summary>
public static class ExplorationScenarios
{
    public const string A = "A";
    public const string B = "B";
    public const string ElementId = "e";
    public const string ListProperty = "list";
    public const int MaxListItems = 3;

    private static readonly string[] TwoWorkspaces = { A, B };

    /// <summary>
    /// Tiny list configuration (one item per side, shallow depth) whose full
    /// transition graph stays small enough for a paper figure.
    /// </summary>
    public static ExplorationScenario PaperList(int maxDepth = 4) =>
        ListScenario("paper-list",
            "List state space, paper-sized: public list starts empty; A can add item x, B item y; depth " + maxDepth,
            new[] { "x" }, new[] { "y" }, maxDepth, 25_000);

    /// <summary>The full bounded list exploration used by the test suite.</summary>
    public static ExplorationScenario List(int maxDepth = 6) =>
        ListScenario("list",
            "List state space: public list starts empty; A can add a1,a2, B can add b1; every insert position, every removal, every commit/update; depth " + maxDepth,
            new[] { "a1", "a2" }, new[] { "b1" }, maxDepth, 25_000);

    /// <summary>All property kinds at once (single, set, map, list, element lifecycle).</summary>
    public static ExplorationScenario Mixed(int maxDepth = 3) => new()
    {
        Name = "mixed",
        Description = "All property kinds: single s, set {m0,m1}, map {k0}, list (max 3), element delete/recreate; depth " + maxDepth,
        Seed = s =>
        {
            s.Apply(A, Ops.Create(ElementId));
            s.Apply(A, Ops.Set(ElementId, "s", "v0"));
            s.Apply(A, Ops.AddSet(ElementId, "set", "m0"));
            s.Apply(A, Ops.Put(ElementId, "map", "k0", "v0"));
            s.Apply(A, Ops.Insert(ElementId, ListProperty, "L0", "L0", null));
        },
        StepsOf = s =>
            MixedEditSteps(s, A, new[] { "a-L" })
                .Concat(MixedEditSteps(s, B, new[] { "b-L" }))
                .Concat(SyncSteps(s, TwoWorkspaces)),
        Label = s => MixedLabel(s, TwoWorkspaces),
        PrivateWorkspaces = TwoWorkspaces,
        MaxDepth = maxDepth,
        MaxExpansions = 25_000
    };

    public static ExplorationScenario ByName(string name, int? maxDepth = null) => name switch
    {
        "paper-list" => maxDepth is { } d ? PaperList(d) : PaperList(),
        "list" => maxDepth is { } d2 ? List(d2) : List(),
        "mixed" => maxDepth is { } d3 ? Mixed(d3) : Mixed(),
        _ => throw new ArgumentException($"Unknown scenario '{name}'. Known: paper-list, list, mixed.")
    };

    private static ExplorationScenario ListScenario(
        string name, string description, string[] poolA, string[] poolB, int maxDepth, int maxExpansions) => new()
    {
        Name = name,
        Description = description,
        Seed = s => s.Apply(A, Ops.Create(ElementId)),
        StepsOf = s =>
            ListEditSteps(s, A, poolA)
                .Concat(ListEditSteps(s, B, poolB))
                .Concat(SyncSteps(s, TwoWorkspaces)),
        Label = s => ListLabel(s, TwoWorkspaces),
        PrivateWorkspaces = TwoWorkspaces,
        MaxDepth = maxDepth,
        MaxExpansions = maxExpansions
    };

    // -------------------------------------------------------- action generators

    /// <summary>
    /// The sync actions enabled in the current state. Update is offered with
    /// both strategies only when the divergence contains a conflict requiring
    /// a winner choice; commit only when it would actually fast-forward.
    /// </summary>
    public static IEnumerable<ExplorationStep> SyncSteps(ModelService service, IReadOnlyList<string> workspaces)
    {
        foreach (var ws in workspaces)
        {
            var lca = service.Tree.Lca(ws, ModelService.PublicWorkspaceId);
            var publicDelta = service.Tree.PathBetween(lca, service.Tree.Head(ModelService.PublicWorkspaceId));
            var childDelta = service.Tree.PathBetween(lca, service.Tree.Head(ws));
            var w = ws;

            if (publicDelta.Count > 0)
            {
                if (ConflictDetector.Detect(publicDelta, childDelta).Any(c => c.RequiresResolution))
                {
                    yield return new ExplorationStep($"{w}:update:child-wins", s => s.Update(w, ResolutionStrategy.ChildWins));
                    yield return new ExplorationStep($"{w}:update:parent-wins", s => s.Update(w, ResolutionStrategy.ParentWins));
                }
                else
                {
                    yield return new ExplorationStep($"{w}:update", s => s.Update(w));
                }
            }
            else if (childDelta.Count > 0)
            {
                yield return new ExplorationStep($"{w}:commit", s =>
                {
                    var commit = s.Commit(w);
                    if (!commit.Success)
                    {
                        throw new InvalidOperationException($"Commit of '{w}' unexpectedly failed: {commit.Reason}");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Every list edit possible in the workspace's current view: insert the
    /// next unused own item at the head and after every alive item (while the
    /// list holds fewer than 3), and remove every alive item.
    /// </summary>
    public static IEnumerable<ExplorationStep> ListEditSteps(ModelService service, string ws, string[] pool)
    {
        var element = service.GetModel(ws).GetElement(ElementId);
        if (element is null)
        {
            yield break;
        }

        var property = element.GetProperty(ListProperty);
        var alive = property?.ListItems ?? Array.Empty<ListNode>();

        var next = pool.FirstOrDefault(id => property?.FindNode(id) is null);
        if (next is not null && alive.Count < MaxListItems)
        {
            yield return new ExplorationStep($"{ws}:ins:{next}@head",
                s => s.Apply(ws, Ops.Insert(ElementId, ListProperty, next, next, null)));
            foreach (var anchor in alive)
            {
                var anchorId = anchor.ItemId;
                yield return new ExplorationStep($"{ws}:ins:{next}@{anchorId}",
                    s => s.Apply(ws, Ops.Insert(ElementId, ListProperty, next, next, anchorId)));
            }
        }

        foreach (var item in alive)
        {
            var itemId = item.ItemId;
            yield return new ExplorationStep($"{ws}:rm:{itemId}",
                s => s.Apply(ws, Ops.RemoveItem(ElementId, ListProperty, itemId)));
        }
    }

    /// <summary>
    /// Edits over every property kind, from the workspace's current view:
    /// singles, a shared set member, a shared map key, the bounded list and the
    /// element lifecycle (delete alive / recreate deleted).
    /// </summary>
    public static IEnumerable<ExplorationStep> MixedEditSteps(ModelService service, string ws, string[] listPool)
    {
        var model = service.GetModel(ws);
        var element = model.GetElement(ElementId);
        if (element is null)
        {
            if (model.GetElementIncludingDeleted(ElementId) is not null)
            {
                yield return new ExplorationStep($"{ws}:create", s => s.Apply(ws, Ops.Create(ElementId)));
            }

            yield break;
        }

        yield return new ExplorationStep($"{ws}:delete", s => s.Apply(ws, Ops.Delete(ElementId)));

        // Single value.
        var ownValue = $"{ws}-v";
        if (element.GetProperty("s")?.SingleValue?.Content != ownValue)
        {
            yield return new ExplorationStep($"{ws}:set", s => s.Apply(ws, Ops.Set(ElementId, "s", ownValue)));
        }

        if (element.GetProperty("s")?.SingleValue is not null)
        {
            yield return new ExplorationStep($"{ws}:unset", s => s.Apply(ws, Ops.Unset(ElementId, "s")));
        }

        // Set membership over a shared member pool.
        var set = element.GetProperty("set");
        if (set?.ContainsSetValue(PropertyValue.String("m1")) != true)
        {
            yield return new ExplorationStep($"{ws}:add:m1", s => s.Apply(ws, Ops.AddSet(ElementId, "set", "m1")));
        }

        foreach (var member in (set?.SetValues ?? Array.Empty<PropertyValue>()).Select(v => v.Content).OrderBy(v => v, StringComparer.Ordinal))
        {
            var m = member;
            yield return new ExplorationStep($"{ws}:rm-set:{m}", s => s.Apply(ws, Ops.RemoveSet(ElementId, "set", m)));
        }

        // Map entries on a shared key.
        var map = element.GetProperty("map");
        if (map?.MapValues.GetValueOrDefault("k0")?.Content != ownValue)
        {
            yield return new ExplorationStep($"{ws}:put:k0", s => s.Apply(ws, Ops.Put(ElementId, "map", "k0", ownValue)));
        }

        foreach (var key in (map?.MapValues.Keys ?? Enumerable.Empty<string>()).OrderBy(k => k, StringComparer.Ordinal))
        {
            var k = key;
            yield return new ExplorationStep($"{ws}:rm-map:{k}", s => s.Apply(ws, Ops.RemoveMap(ElementId, "map", k)));
        }

        foreach (var step in ListEditSteps(service, ws, listPool))
        {
            yield return step;
        }
    }

    // ---------------------------------------------------------------- labeling

    /// <summary>Compact state label for list scenarios: alive items per workspace plus divergence.</summary>
    private static string ListLabel(ModelService service, IReadOnlyList<string> workspaces)
    {
        var sb = new StringBuilder();
        sb.Append("P:").Append(RenderList(service.GetModel(ModelService.PublicWorkspaceId)));
        foreach (var ws in workspaces)
        {
            sb.Append(' ').Append(ws).Append(':').Append(RenderList(service.GetModel(ws)));
            sb.Append(Divergence(service, ws));
        }

        return sb.ToString();
    }

    private static string MixedLabel(ModelService service, IReadOnlyList<string> workspaces)
    {
        var sb = new StringBuilder();
        sb.Append("P:").Append(RenderMixed(service.GetModel(ModelService.PublicWorkspaceId)));
        foreach (var ws in workspaces)
        {
            sb.Append(' ').Append(ws).Append(':').Append(RenderMixed(service.GetModel(ws)));
            sb.Append(Divergence(service, ws));
        }

        return sb.ToString();
    }

    private static string Divergence(ModelService service, string ws)
    {
        var lca = service.Tree.Lca(ws, ModelService.PublicWorkspaceId);
        var ahead = service.Tree.PathBetween(lca, service.Tree.Head(ws)).Count;
        var behind = service.Tree.PathBetween(lca, service.Tree.Head(ModelService.PublicWorkspaceId)).Count;
        return ahead == 0 && behind == 0 ? "" : $"↑{ahead}↓{behind}";
    }

    private static string RenderList(ModelState model)
    {
        var element = model.GetElement(ElementId);
        if (element is null)
        {
            return model.GetElementIncludingDeleted(ElementId) is null ? "?" : "×";
        }

        // Tombstones are rendered with a dagger: they are invisible to the
        // user but still anchor future inserts, so states that differ only in
        // tombstones behave differently and are distinct graph nodes.
        var items = element.GetProperty(ListProperty)?.ListNodes
            .Select(n => n.IsDeleted ? "†" + n.Value.Content : n.Value.Content) ?? Enumerable.Empty<string>();
        return "[" + string.Join(" ", items) + "]";
    }

    private static string RenderMixed(ModelState model)
    {
        var element = model.GetElement(ElementId);
        if (element is null)
        {
            return model.GetElementIncludingDeleted(ElementId) is null ? "?" : "×";
        }

        var s = element.GetProperty("s")?.SingleValue?.Content ?? "∅";
        var set = string.Join(",", (element.GetProperty("set")?.SetValues ?? Array.Empty<PropertyValue>())
            .Select(v => v.Content).OrderBy(v => v, StringComparer.Ordinal));
        var map = string.Join(",", (element.GetProperty("map")?.MapValues ?? new Dictionary<string, PropertyValue>())
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.Content}"));
        var list = string.Join(" ", element.GetProperty(ListProperty)?.ListItems.Select(i => i.Value.Content) ?? Enumerable.Empty<string>());
        return $"(s={s} {{{set}}} {{{map}}} [{list}])";
    }
}
