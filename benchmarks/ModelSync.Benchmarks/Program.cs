using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ModelSync.Core;

// Performance benchmarks for the thesis evaluation chapter.
//
//   dotnet run -c Release --project benchmarks/ModelSync.Benchmarks            # full run
//   dotnet run -c Release --project benchmarks/ModelSync.Benchmarks -- --job short --filter '*Detect*'
//
// The three suites produce the scalability curves the approach claims:
//  - conflict detection is O(n + m) in the two DELTAS, never in total history;
//  - a full update/commit round is linear in the concurrent edit count;
//  - fresh checkout (replay) is linear in HISTORY length — the known
//    non-incremental cost of the operation-based representation.

BenchmarkSwitcher
    .FromTypes(new[] { typeof(ConflictDetectionBenchmarks), typeof(SyncRoundBenchmarks), typeof(ReplayBenchmarks) })
    .Run(args);

public static class OpFactory
{
    public static Operation Create(string id) => new()
    {
        Id = Guid.NewGuid(), Type = OperationType.CreateElement, WorkspaceId = "", ElementId = id
    };

    public static Operation Set(string id, string property, string value) => new()
    {
        Id = Guid.NewGuid(), Type = OperationType.SetProperty, WorkspaceId = "",
        ElementId = id, PropertyName = property, Value = PropertyValue.String(value)
    };

    public static Operation Insert(string id, string property, string itemId, string value, string? after) => new()
    {
        Id = Guid.NewGuid(), Type = OperationType.InsertListItem, WorkspaceId = "",
        ElementId = id, PropertyName = property, ItemId = itemId, AfterItemId = after,
        Value = PropertyValue.String(value)
    };

    /// <summary>A representative delta: element creates, property sets and list inserts.</summary>
    public static List<Operation> Delta(int size, string prefix)
    {
        var ops = new List<Operation>(size);
        for (var i = 0; i < size; i++)
        {
            ops.Add((i % 3) switch
            {
                0 => Create($"{prefix}-e{i}"),
                1 => Set($"{prefix}-e{i - 1}", "name", $"{prefix}-v{i}"),
                _ => Insert($"{prefix}-e{i - 2}", "items", $"{prefix}-i{i}", $"{prefix}-x{i}", null)
            });
        }

        return ops;
    }

    /// <summary>Concurrent edits of both sides on shared elements: every op conflicts.</summary>
    public static List<Operation> ConflictingDelta(int size, string prefix, int sharedElements)
    {
        var ops = new List<Operation>(size);
        for (var i = 0; i < size; i++)
        {
            ops.Add(Set($"shared-{i % sharedElements}", $"p{i % 7}", $"{prefix}-{i}"));
        }

        return ops;
    }
}

/// <summary>Detection cost as a function of the two delta sizes.</summary>
[MemoryDiagnoser]
public class ConflictDetectionBenchmarks
{
    [Params(10, 100, 1_000)]
    public int DeltaSize;

    private List<Operation> _parentDisjoint = null!;
    private List<Operation> _childDisjoint = null!;
    private List<Operation> _parentConflicting = null!;
    private List<Operation> _childConflicting = null!;

    [GlobalSetup]
    public void Setup()
    {
        _parentDisjoint = OpFactory.Delta(DeltaSize, "p");
        _childDisjoint = OpFactory.Delta(DeltaSize, "c");
        _parentConflicting = OpFactory.ConflictingDelta(DeltaSize, "p", sharedElements: 10);
        _childConflicting = OpFactory.ConflictingDelta(DeltaSize, "c", sharedElements: 10);
    }

    [Benchmark]
    public int Detect_DisjointDeltas() =>
        ConflictDetector.Detect(_parentDisjoint, _childDisjoint).Count;

    [Benchmark]
    public int Detect_FullyConflictingDeltas() =>
        ConflictDetector.Detect(_parentConflicting, _childConflicting).Count;
}

/// <summary>One full synchronization round (A commits, B updates + resolves + commits).</summary>
[MemoryDiagnoser]
public class SyncRoundBenchmarks
{
    [Params(10, 100, 1_000)]
    public int ConcurrentEdits;

    [Benchmark]
    public int UpdateCommitRound()
    {
        var service = new ModelService();
        service.Checkout("A");
        for (var i = 0; i < 20; i++)
        {
            service.Apply("A", OpFactory.Create($"e{i}"));
        }

        service.Commit("A");
        service.Checkout("B");
        service.Update("B");

        for (var i = 0; i < ConcurrentEdits; i++)
        {
            service.Apply("A", OpFactory.Set($"e{i % 20}", $"p{i % 5}", $"a{i}"));
            service.Apply("B", OpFactory.Set($"e{i % 20}", $"p{i % 5}", $"b{i}"));
        }

        service.Commit("A");
        var update = service.Update("B", ResolutionStrategy.ChildWins);
        service.Commit("B");
        return update.Conflicts.Count;
    }
}

/// <summary>Fresh checkout = full history replay: the linear-in-history cost.</summary>
[MemoryDiagnoser]
public class ReplayBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int HistoryLength;

    private ModelService _service = null!;
    private int _checkoutCounter;

    [GlobalSetup]
    public void Setup()
    {
        _service = new ModelService();
        _service.Checkout("A");
        foreach (var op in OpFactory.Delta(HistoryLength, "h"))
        {
            _service.Apply("A", op);
        }

        _service.Commit("A");
    }

    [Benchmark]
    public int FreshCheckoutReplaysHistory() =>
        _service.Checkout($"fresh-{_checkoutCounter++}").Elements.Count();
}
