using System.Text;

namespace ModelSync.Exploration;

/// <summary>
/// Exports an explored state space as Graphviz DOT (for paper figures, render
/// with `dot -Tsvg` / `dot -Tpdf`) and as Mermaid (rendered natively by
/// GitHub-flavored markdown).
/// </summary>
public static class GraphExport
{
    private const string InSyncFill = "#d5f0d9";
    private const string DivergedFill = "#fdf3d0";
    private const string ConflictedFill = "#f9d7d7";

    public static string ToDot(ExplorationResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("digraph statespace {");
        sb.AppendLine($"  // {Escape(result.Scenario.Description)}");
        sb.AppendLine($"  label=\"ModelSync state space — {Escape(result.Scenario.Name)}\\n" +
                      $"{result.Stats.UniqueStates} states, {result.Graph.Transitions.Count} transitions " +
                      $"(green=in-sync, yellow=diverged, red=conflicted)\";");
        sb.AppendLine("  labelloc=t;");
        sb.AppendLine("  rankdir=TB;");
        sb.AppendLine("  node [shape=box, style=\"rounded,filled\", fontname=\"Helvetica\", fontsize=9, margin=\"0.06,0.04\"];");
        sb.AppendLine("  edge [fontname=\"Helvetica\", fontsize=8, color=\"#555555\"];");

        foreach (var node in result.Graph.Nodes)
        {
            var fill = node.Kind switch
            {
                StateKind.InSync => InSyncFill,
                StateKind.Diverged => DivergedFill,
                _ => ConflictedFill
            };
            var shape = node == result.Graph.InitialNode ? ", peripheries=2" : "";
            sb.AppendLine($"  {node.Id} [label=\"{Escape(node.Label)}\", fillcolor=\"{fill}\"{shape}];");
        }

        foreach (var t in result.Graph.Transitions)
        {
            sb.AppendLine($"  {t.FromId} -> {t.ToId} [label=\"{Escape(t.ActionName)}\"];");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string ToMermaid(ExplorationResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: \"ModelSync state space — {result.Scenario.Name} " +
                      $"({result.Stats.UniqueStates} states, {result.Graph.Transitions.Count} transitions)\"");
        sb.AppendLine("---");
        sb.AppendLine("flowchart TD");
        sb.AppendLine("  classDef insync fill:#d5f0d9,stroke:#2e7d32,color:#1b3a1f;");
        sb.AppendLine("  classDef diverged fill:#fdf3d0,stroke:#b58900,color:#4a3d10;");
        sb.AppendLine("  classDef conflicted fill:#f9d7d7,stroke:#c62828,color:#4a1414;");

        foreach (var node in result.Graph.Nodes)
        {
            var cls = node.Kind switch
            {
                StateKind.InSync => "insync",
                StateKind.Diverged => "diverged",
                _ => "conflicted"
            };
            sb.AppendLine($"  {node.Id}[\"{MermaidEscape(node.Label)}\"]:::{cls}");
        }

        foreach (var t in result.Graph.Transitions)
        {
            sb.AppendLine($"  {t.FromId} -->|\"{MermaidEscape(t.ActionName)}\"| {t.ToId}");
        }

        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string MermaidEscape(string text) =>
        text.Replace("\"", "#quot;").Replace("[", "&#91;").Replace("]", "&#93;");
}
