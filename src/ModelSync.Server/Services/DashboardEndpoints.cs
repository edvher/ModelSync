using ModelSync.Core;

namespace ModelSync.Server.Services;

/// <summary>
/// A small human-facing dashboard: JSON APIs plus a self-contained HTML page
/// that renders the live operation tree, the workspace heads and the pairwise
/// conflict awareness.
/// </summary>
public static class DashboardEndpoints
{
    public static void MapDashboard(this WebApplication app)
    {
        app.MapGet("/health", () => "ModelSync server is running.");

        app.MapGet("/api/workspaces", (ModelService service) =>
            Results.Json(service.Workspaces.OrderBy(w => w, StringComparer.Ordinal)));

        app.MapGet("/api/tree", (ModelService service) =>
        {
            var snapshot = service.Tree.Snapshot();
            var heads = snapshot.Heads.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
            var nodes = snapshot.Nodes.Values.Select(node => new
            {
                id = node.Id.ToString(),
                parent = node.Parent?.ToString(),
                children = node.Children.Select(c => c.ToString()),
                label = node.Operation.ToString(),
                workspace = node.Operation.WorkspaceId,
                isResolution = node.Operation.IsResolution,
                heads = snapshot.Heads.Where(h => h.Value == node.Id).Select(h => h.Key)
            });

            return Results.Json(new { root = snapshot.RootId.ToString(), heads, nodes });
        });

        app.MapGet("/api/model/{workspaceId}", (string workspaceId, ModelService service) =>
        {
            var model = service.Checkout(workspaceId);
            var elements = model.Elements.Select(element => new
            {
                id = element.Id,
                type = element.TypeId,
                properties = element.Properties.Values.Select(property => new
                {
                    name = property.Name,
                    cardinality = property.Cardinality.ToString(),
                    single = property.SingleValue?.Content,
                    set = property.SetValues.Select(v => v.Content),
                    map = property.MapValues.ToDictionary(p => p.Key, p => p.Value.Content),
                    list = property.ListItems.Select(i => new { i.ItemId, value = i.Value.Content })
                })
            });

            return Results.Json(new { workspace = workspaceId, elements });
        });

        app.MapGet("/api/conflicts", (string a, string b, ConflictAwarenessService awareness) =>
        {
            var conflicts = awareness.GetConflicts(a, b).Select(conflict => new
            {
                category = conflict.Category.ToString(),
                mergeType = conflict.MergeType.ToString().ToUpperInvariant(),
                severity = conflict.Severity.ToString(),
                policy = conflict.Policy.ToString(),
                requiresResolution = conflict.RequiresResolution,
                key = conflict.ConflictKey,
                parent = conflict.ParentOperation.ToString(),
                child = conflict.ChildOperation.ToString()
            });

            return Results.Json(conflicts);
        });

        app.MapGet("/", () => Results.Content(DashboardHtml, "text/html"));
    }

    private const string DashboardHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>ModelSync Dashboard</title>
<style>
  :root { color-scheme: light dark; }
  body { font-family: ui-sans-serif, system-ui, sans-serif; margin: 1.5rem; }
  h1 { font-size: 1.3rem; }
  h2 { font-size: 1rem; margin-top: 1.5rem; }
  .cols { display: flex; gap: 2rem; flex-wrap: wrap; align-items: flex-start; }
  ul.tree { list-style: none; padding-left: 1rem; border-left: 1px solid #8884; margin: 0.2rem 0; }
  .op { padding: 0.1rem 0.3rem; }
  .badge { display: inline-block; border-radius: 0.6rem; padding: 0 0.45rem; margin-left: 0.4rem;
           font-size: 0.75rem; color: #fff; background: #2563eb; }
  .badge.public { background: #16a34a; }
  .resolution { color: #b45309; }
  table { border-collapse: collapse; }
  td, th { border: 1px solid #8884; padding: 0.25rem 0.5rem; font-size: 0.85rem; }
  .real { color: #dc2626; font-weight: 600; }
  .pseudo { color: #6b7280; }
  code { font-size: 0.85rem; }
</style>
</head>
<body>
<h1>ModelSync &mdash; live operation tree</h1>
<div class="cols">
  <div>
    <h2>Operation tree (one branch per workspace)</h2>
    <div id="tree">loading&hellip;</div>
  </div>
  <div>
    <h2>Conflict awareness</h2>
    <div id="conflicts">loading&hellip;</div>
  </div>
</div>
<script>
async function refresh() {
  try {
    const tree = await (await fetch('/api/tree')).json();
    const byId = {};
    for (const n of tree.nodes) byId[n.id] = n;
    function render(id) {
      const n = byId[id];
      if (!n) return '';
      const badges = (n.heads || []).map(h =>
        `<span class="badge ${h === 'P' ? 'public' : ''}">${h}</span>`).join('');
      const cls = n.isResolution ? 'op resolution' : 'op';
      const children = (n.children || []).map(render).join('');
      return `<li><span class="${cls}"><code>${n.label}</code></span>${badges}` +
             (children ? `<ul class="tree">${children}</ul>` : '') + '</li>';
    }
    document.getElementById('tree').innerHTML = `<ul class="tree">${render(tree.root)}</ul>`;

    const workspaces = await (await fetch('/api/workspaces')).json();
    const privates = workspaces.filter(w => w !== 'P');
    let html = '';
    for (const w of privates) {
      const conflicts = await (await fetch(`/api/conflicts?a=P&b=${encodeURIComponent(w)}`)).json();
      html += `<h3>P &harr; ${w} (${conflicts.length})</h3>`;
      if (conflicts.length) {
        html += '<table><tr><th>Severity</th><th>Type</th><th>Category</th><th>Parent op</th><th>Child op</th></tr>' +
          conflicts.map(c =>
            `<tr><td class="${c.severity.toLowerCase()}">${c.severity}</td><td>${c.mergeType}</td>` +
            `<td>${c.category}</td><td><code>${c.parent}</code></td><td><code>${c.child}</code></td></tr>`).join('') +
          '</table>';
      }
    }
    document.getElementById('conflicts').innerHTML = html || 'no private workspaces yet';
  } catch (e) {
    console.error(e);
  }
}
refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}
