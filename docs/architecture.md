# Architecture

ModelSync is "Google Docs for models": any number of clients edit models — and
their metamodels — concurrently; a central server keeps every replica
convergent through incremental operations only.

## System overview

```mermaid
flowchart LR
  classDef client fill:#e8eefc,stroke:#3b5bdb,color:#1c2f66;
  classDef server fill:#d5f0d9,stroke:#2e7d32,color:#1b3a1f;
  classDef core fill:#fdf3d0,stroke:#b58900,color:#4a3d10;
  classDef ui fill:#f3e6f7,stroke:#7b1fa2,color:#3d1048;

  subgraph Alice["Client Alice (ModelSync.Sdk)"]
    AS["WorkspaceSession<br/>local replica, rebuilt purely<br/>by operation replay"]:::client
    AC["ModelSyncClient<br/>typed gRPC wrapper"]:::client
    AS --- AC
  end

  subgraph Bob["Client Bob (ModelSync.Sdk)"]
    BS["WorkspaceSession"]:::client
    BC["ModelSyncClient"]:::client
    BS --- BC
  end

  subgraph Server["ModelSync.Server"]
    G["gRPC ModelSyncService — h2c :5001<br/>Checkout · Apply · Update · Commit<br/>Subscribe (replay + live) · Awareness"]:::server
    DASH["Live dashboard + JSON APIs — :5000<br/>operation tree, per-workspace heads,<br/>resolutions highlighted, awareness table"]:::ui

    subgraph Core["ModelSync.Core"]
      MS["ModelService<br/>the central ordering mechanism:<br/>all operations of all workspaces<br/>run through it (one global order)"]:::core
      OT["OperationTree<br/>history as a growing tree:<br/>main branch = public workspace,<br/>one branch per private workspace;<br/>LCA, path replay, re-attachment"]:::core
      CD["ConflictDetector<br/>O(n+m) net-effect indexing,<br/>REAL/PSEUDO × MMC/DMC/MDC/DDC"]:::core
      CR["ConflictResolver<br/>resolutions as re-executed operations:<br/>winner closures, re-anchoring,<br/>delete inversion"]:::core
      CA["ConflictAwarenessService<br/>pairwise conflicts between any two<br/>workspaces before they sync"]:::core
      ST["ModelState per workspace<br/>materialized by applying operations;<br/>soft deletes, tombstoned lists"]:::core
    end

    G --> MS
    DASH --> MS
    MS --> OT
    MS --> CD
    MS --> CR
    MS --> ST
    CA --> CD
    G --> CA
  end

  AC -- "operations / update / commit" --> G
  BC -- "operations / update / commit" --> G
  G -. "live public operation stream" .-> AC
  G -. "live public operation stream" .-> BC
```

## The operation history is a growing tree

Workspaces form a **star**: every private workspace branches off the public
branch and rejoins it through update/commit. Replaying any branch from the
root reproduces that workspace's model exactly.

```mermaid
flowchart LR
  classDef pub fill:#d5f0d9,stroke:#2e7d32,color:#1b3a1f;
  classDef priv fill:#e8eefc,stroke:#3b5bdb,color:#1c2f66;
  classDef res fill:#fdf3d0,stroke:#b58900,color:#4a3d10;

  R((root)):::pub --> O1["op₁"]:::pub --> O2["op₂<br/>(branching point / LCA of Bob)"]:::pub --> O3["op₃<br/>public head after Alice's commit"]:::pub
  O3 --> B1["Bob's op b₁<br/>(re-attached by update)"]:::priv --> B2["b₂"]:::priv --> RES["resolution ops appended by update —<br/>when Bob commits, the public head<br/>fast-forwards here"]:::res
```

- **Checkout** creates a branch at the current public head and materializes
  the model by replaying the branch path.
- **Apply** validates one new operation against the workspace's current model,
  executes it and appends it to the branch.
- **Update** computes both deltas from the LCA, detects conflicts between
  them, replays the public delta, **re-executes the local delta** (so the live
  model equals the branch replay order), re-attaches the branch onto the
  public head and appends deterministic resolution operations.
- **Commit** is fast-forward only: the public branch replays the private delta
  — the private operations concatenated with their resolutions — and the
  public head moves to the private head.

Because every replica ends up executing the same operation sequence in the
same order, all replicas converge — including soft-deleted (tombstone) state.
See [list-synchronization.md](list-synchronization.md) for the list algorithm
and [conflict-catalog.md](conflict-catalog.md) for every conflict and its
outcome.

## Projects

| Project | Role |
|---|---|
| `src/ModelSync.Core` | Model state, operations, operation tree, conflict detection/resolution, awareness — no I/O. |
| `src/ModelSync.Server` | gRPC service (:5001) + live dashboard and JSON APIs (:5000); opt-in demo seeder. |
| `src/ModelSync.Sdk` | `ModelSyncClient` + `WorkspaceSession`: typed incremental edit API over a replayed local replica. |
| `src/ModelSync.Exploration` | Reusable state-space explorer + DOT/Mermaid graph generator for docs and paper figures. |
| `tests/ModelSync.Tests` | Unit, scenario, E2E, exhaustive DFS exploration and randomized property tests. |
| `benchmarks/ModelSync.Benchmarks` | BenchmarkDotNet suites: detection, sync round, replay scalability. |
