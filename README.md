# ModelSync

ModelSync is a collaborative model synchronization server — "Google Docs for
models". Multiple clients work concurrently on models **and metamodels** in
private workspaces and synchronize through a shared public workspace, with
incremental, operation-based conflict detection and deterministic resolution.

The implementation follows the operation-based collaboration approach of the
DesignSpace research line (flexible operation-based infrastructure,
conflict-based change awareness, incremental syntactic conflict detection and
resolution):

- **Everything is an operation.** A workspace can only change through discrete
  CMD operations (create/delete element, set/unset property, add/remove set
  item, put/remove map entry, insert/remove list item). There is no state
  upload — replaying a branch's operations *is* the model.
- **The operation history is a tree.** Every workspace is a branch with a head
  pointer; `P` is the public branch. Checkout starts a branch at the public
  head; update re-attaches the private branch onto the new public head;
  commit fast-forwards the public head. Replaying the path from the root to a
  head reproduces that workspace's model exactly.
- **Domain-agnostic property–value model** (streamlined MOF): elements with
  single-valued, set, map and list properties. Types are ordinary elements
  referenced via `ElementTypeId`, so metamodels synchronize through the same
  operations as models.
- **Lists synchronize incrementally** through stable item ids and anchor-based
  inserts (`insert item X after item Y`). Removals tombstone the node so that
  concurrent anchors stay executable and every replica converges. Re-executing
  an insert *moves* the item, which is what makes resolution deterministic.
- **Conflicts are detected between concurrent operation sequences** (the two
  deltas below the branching point, found via LCA) in O(n+m) using
  per-property-type keys, classified REAL vs PSEUDO and MMC/DMC/MDC/DDC with
  Ignore/Choose/Merge policies.
- **Resolutions are operations too.** Every non-commutative conflict yields a
  resolution operation appended to the updating branch (winner re-execution,
  delete-inversion into a resurrecting create, list re-anchoring, follower
  chain cloning). Because resolutions replay everywhere, all replicas converge
  to the same state — the invariant the test suite enforces exhaustively.

## Projects

| Project | Purpose |
|---|---|
| `src/ModelSync.Core` | Model state, operations, tree-shaped operation history, conflict detection/resolution, workspace service, conflict awareness. Pure .NET, no I/O. |
| `src/ModelSync.Server` | gRPC server (checkout/apply/update/commit/subscribe/awareness) plus a live HTML dashboard of the operation tree. |
| `src/ModelSync.Sdk` | Client SDK: low-level `ModelSyncClient` and high-level `WorkspaceSession` with a local replica reconstructed purely by operation replay. |
| `tests/ModelSync.Tests` | xUnit suite: unit tests, the full conflict catalog, synchronization scenarios, a bounded state-space exploration oracle, and end-to-end tests with two clients against the real server. |

## Running the server

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/).

```bash
dotnet run --project src/ModelSync.Server/ModelSync.Server.csproj
```

- gRPC endpoint: `http://localhost:5001` (HTTP/2 without TLS)
- Dashboard: `http://localhost:5000` — live operation tree (one branch per
  workspace, resolution operations highlighted) and pairwise conflict
  awareness, auto-refreshing.
- JSON APIs: `/api/tree`, `/api/workspaces`, `/api/model/{workspace}`,
  `/api/conflicts?a=P&b=A`, `/health`.
- Optional demo scenario: start with `--seed-demo` (or `MODELSYNC_SEED_DEMO=1`)
  to populate a fresh server with a public model, two diverging workspaces and
  a brewing conflict — seeded through the regular workspace API at startup, so
  it behaves exactly like real client activity.

## Using the SDK

```csharp
using ModelSync.Core;
using ModelSync.Sdk;

// Two engineers, each in a private workspace.
await using var alice = await WorkspaceSession.ConnectAsync("http://localhost:5001", "alice");
await using var bob   = await WorkspaceSession.ConnectAsync("http://localhost:5001", "bob");

// Alice models a metamodel and an instance — types are elements too.
var classType = await alice.CreateElementAsync();
await alice.SetPropertyAsync(classType, "name", PropertyValue.String("Class"));
var monitor = await alice.CreateElementAsync(typeId: classType);
await alice.SetPropertyAsync(monitor, "name", PropertyValue.String("HeartbeatMonitor"));
await alice.AppendListItemAsync(monitor, "methods", PropertyValue.String("measureHeartbeat"));
await alice.CommitAsync();                       // push to the public workspace

var update = await bob.UpdateAsync();            // pull -> replica now has everything
// bob.Model is a full local replica (ModelState), rebuilt purely from operations.

// Concurrent conflicting edits:
await alice.SetPropertyAsync(monitor, "name", PropertyValue.String("scanHeartbeat"));
await bob.SetPropertyAsync(monitor, "name", PropertyValue.String("sampleHeartbeat"));
await alice.CommitAsync();

// Awareness: the server knows the two workspaces collide before any sync.
var brewing = await bob.GetConflictsWithAsync("alice");

// Bob must update before committing; conflicts are resolved deterministically.
var result = await bob.UpdateAsync(ResolutionStrategy.ChildWins);
foreach (var conflict in result.Conflicts)
    Console.WriteLine(conflict);                 // REAL MMC [SingleValue] ...
await bob.CommitAsync();
await alice.UpdateAsync();                       // everyone converged
```

## Conflict handling

Detection compares the public delta (parent) with the private delta (child)
below their branching point, keyed per property semantics:

| Category | Key | REAL when | Resolution |
|---|---|---|---|
| Single value | element + property | different values set, or set vs unset | winner's operation re-executed |
| Element existence | element | delete vs concurrent modify/resurrect | binary: delete re-asserted, or inverted into a resurrecting create (property changes are always kept) |
| Set membership | element + property + value | never (add/remove of the same member is a pseudo conflict, but non-commutative — a resolution still enforces the winner) | winner re-executed |
| Map entry | element + property + key | different values for the same key, put vs remove | winner re-executed |
| List order | element + property + anchor / item id | two inserts at the same anchor, or the same item at different anchors | winner's insert **and its follower chain** re-executed (the winner ends up first) |
| List anchor deleted | insert whose anchor was removed concurrently | always | algorithmic, no choice: insert re-anchored to the closest alive predecessor |
| List anchor moved | insert whose anchor item was moved concurrently | always | algorithmic, no choice: the dependent insert (and its followers) re-executed so it follows the anchor |

Strategies: `ChildWins` (default — the updating workspace) or `ParentWins`
(the public side). Pseudo conflicts (identical outcomes, delete-delete, …)
are reported for awareness but need no resolution.

Detection classifies each branch by its **net effect** — the last surviving
operation per element, property slot, set member, map key and list item — so
superseded intermediate steps ("set then unset", "insert then remove", a move
of the same item) never produce phantom conflicts, matching the thesis rule
*Delete ≙ (Modify\*) Delete*.

Element deletion is *soft*: a delete hides the element, operations from
history still replay onto it, and a later create with the same id resurrects
it with its retained properties. For list items, delete always wins.

## Tests

```bash
dotnet test
```

The suite (205 tests) covers:

- model/state semantics incl. tombstoned lists and resurrect behavior,
- operation tree branching, LCA, path computation and re-attachment,
- the complete conflict-classification catalog (REAL/PSEUDO × MMC/DMC/MDC/DDC
  per property type),
- synchronization scenarios mirroring the thesis running examples (including
  the list POC outcomes `[A,X,U,V,B,C]` / `[A,U,V,X,B,C]`),
- a bounded **state-space exploration oracle**: all combinations of concurrent
  atomic edits on both sides, for both strategies, asserting convergence of
  public, both privates and a fresh replayed checkout,
- end-to-end tests: real gRPC server with two connected clients (Alice & Bob)
  editing metamodel + model concurrently, updating, committing, streaming
  public operations and querying awareness,
- regression tests from an adversarial audit: branching follower chains,
  multiple competing inserts at one anchor, moved anchors, delete-then-
  resurrect nets, superseded-operation classification and awareness cache
  invalidation.
