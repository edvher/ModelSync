using ModelSync.Exploration;

// State-space graph generator for papers and docs.
//
//   dotnet run --project src/ModelSync.Exploration -- [scenario ...] [--depth N] [--out DIR]
//
// Scenarios: paper-list (default figure), list, mixed, or "all".
// Writes <out>/state-space-<scenario>.dot and .mmd and prints exploration stats.

var scenarios = new List<string>();
var outDir = "docs/figures";
int? depth = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out":
            outDir = args[++i];
            break;
        case "--depth":
            depth = int.Parse(args[++i]);
            break;
        case "--help" or "-h":
            Console.WriteLine("usage: modelsync-exploration [paper-list|list|mixed|all ...] [--depth N] [--out DIR]");
            return 0;
        default:
            scenarios.Add(args[i]);
            break;
    }
}

if (scenarios.Count == 0)
{
    scenarios.Add("all");
}

if (scenarios.Remove("all"))
{
    scenarios.AddRange(new[] { "paper-list", "list", "mixed" });
}

Directory.CreateDirectory(outDir);

foreach (var name in scenarios.Distinct())
{
    var scenario = ExplorationScenarios.ByName(name, depth);
    var started = System.Diagnostics.Stopwatch.StartNew();
    var result = StateSpaceExplorer.Explore(scenario);
    started.Stop();

    var dotPath = Path.Combine(outDir, $"state-space-{scenario.Name}.dot");
    var mmdPath = Path.Combine(outDir, $"state-space-{scenario.Name}.mmd");
    File.WriteAllText(dotPath, GraphExport.ToDot(result));
    File.WriteAllText(mmdPath, GraphExport.ToMermaid(result));

    var stats = result.Stats;
    Console.WriteLine($"[{scenario.Name}] {scenario.Description}");
    Console.WriteLine(
        $"  states: {stats.UniqueStates} (in-sync {stats.InSyncStates}, diverged {stats.DivergedStates}, conflicted {stats.ConflictedStates}), " +
        $"transitions: {result.Graph.Transitions.Count}, visits: {stats.TotalVisits} ({stats.RevisitedStates} states reached via multiple paths)");
    Console.WriteLine($"  conflict coverage: categories [{string.Join(", ", stats.Categories.OrderBy(c => c))}], " +
                      $"merge types [{string.Join(", ", stats.MergeTypes.OrderBy(c => c))}]");
    Console.WriteLine($"  wrote {dotPath} and {mmdPath} in {started.ElapsedMilliseconds} ms");
    Console.WriteLine($"  render: dot -Tsvg {dotPath} -o {Path.ChangeExtension(dotPath, \".svg\")}");
}

return 0;
