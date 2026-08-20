# Conflict catalog

Every possible conflict of the current metamodel (elements with single-valued,
set-valued, map-valued and list-valued properties; types are elements
themselves) and its outcome depending on which side wins. Derived directly
from `ConflictDetector` and `ConflictResolver`; each block names the tests
that verify it.

**Reading the tables.** *Parent* is the public side (the delta the updating
workspace pulls in), *Child* is the updating private workspace's own delta.
Both deltas are first reduced to their **net effect** per element, slot and
list item (*Delete ≙ Modify\* Delete*), so superseded intermediate operations
never conflict. Classification: **MMC** modify/modify, **DMC** delete/modify,
**MDC** modify/delete, **DDC** delete/delete; **Real** = the outcomes
genuinely compete, **Pseudo** = same intent or a deterministic rule decides;
policy **Ignore** = no resolution operation needed (the pair commutes),
**Choose** = a winner strategy picks a side, **Merge** = a resolution is
required but is strategy-independent. Resolutions are always *new operations*
appended to the updating branch; "re-execute" exploits that re-executing an
operation deterministically re-asserts its effect.

A general subsumption rule: keyed-slot conflicts on an element that either
side **net-deleted** are governed by that element's existence conflict —
property edits always execute (onto the tombstone if needed); only the
existence resolution decides visibility.

## Element existence

Verified by `ConflictDetectionTests`, `StateSpaceOracleTests.SingleValueExploration`,
`SchoolScenarioTests.Graduation…`, `RegressionConvergenceTests.Repro9/10`.

| Parent (net) | Child (net) | Type | Severity | Policy | Resolution | Result ChildWins | Result ParentWins |
|---|---|---|---|---|---|---|---|
| create `T₁` | create `T₁` (same type) | MMC | Pseudo | Ignore | — | element exists with type `T₁` | same |
| create `T₁` | create `T₂` (different type) | MMC | Real | Choose | re-execute winner's create | type `T₂` | type `T₁` |
| delete | delete | DDC | Pseudo | Ignore | — | deleted (tombstone keeps last-written properties) | same |
| delete | create / resurrect | DMC | Real | Choose | winner's delete, or resurrecting create | alive (resurrected, properties retained) | deleted |
| create / resurrect | delete | MDC | Real | Choose | mirror of DMC | deleted | alive |
| delete | constructive property edit (set/add/put/insert) | DMC | Real | Choose | delete re-asserted, or delete inverted into resurrecting create | alive, child's edits visible | deleted; child's edits retained on the tombstone |
| delete | destructive-only property edit (unset/remove) | DMC | Pseudo | Ignore | — | deleted (child destroyed content anyway) | same |
| constructive property edit | delete | MDC | Real | Choose | mirror | deleted; parent's edits on tombstone | alive, parent's edits visible |
| destructive-only property edit | delete | MDC | Pseudo | Ignore | — | deleted | same |

## Single-valued property (same element, same property)

Verified by `ConflictDetectionTests`, `StateSpaceOracleTests.SingleValueExploration`
(all 5×5 action combinations × both strategies).

| Parent (net) | Child (net) | Type | Severity | Policy | Resolution | Result ChildWins | Result ParentWins |
|---|---|---|---|---|---|---|---|
| set `v` | set `v` (equal values) | MMC | Pseudo | Ignore | — | `v` | `v` |
| set `v₁` | set `v₂` | MMC | Real | Choose | re-execute winner's set | `v₂` | `v₁` |
| set `v₁` | unset | MDC | Real | Choose | re-execute winner | unset (∅) | `v₁` |
| unset | set `v₂` | DMC | Real | Choose | re-execute winner | `v₂` | unset (∅) |
| unset | unset | DDC | Pseudo | Ignore | — | ∅ | ∅ |

## Set membership (same element, same property, same member)

Different members never conflict (sets are unordered, membership is the key).
Verified by `ConflictDetectionTests`, `SchoolScenarioTests` (subjects),
`DeepStateSpaceExplorationTests` (mixed scenario).

| Parent (net) | Child (net) | Type | Severity | Policy | Resolution | Result ChildWins | Result ParentWins |
|---|---|---|---|---|---|---|---|
| add `m` | add `m` | MMC | Pseudo | Ignore | — | `m` present | `m` present |
| remove `m` | remove `m` | DDC | Pseudo | Ignore | — | `m` absent | `m` absent |
| add `m` | remove `m` | MDC | Pseudo | **Merge** | re-execute winner (non-commutative pair) | `m` absent | `m` present |
| remove `m` | add `m` | DMC | Pseudo | **Merge** | re-execute winner | `m` present | `m` absent |

Add/remove of the same member is *pseudo* (the operations' preconditions are
mutually exclusive states, not competing intents) but non-commutative, so a
resolution operation is still required for convergence.

## Map entries (same element, same property, same key)

Different keys never conflict. Verified by `ConflictDetectionTests`,
`SchoolScenarioTests` (grades, office hours), mixed exploration.

| Parent (net) | Child (net) | Type | Severity | Policy | Resolution | Result ChildWins | Result ParentWins |
|---|---|---|---|---|---|---|---|
| put `k→v` | put `k→v` (equal values) | MMC | Pseudo | Ignore | — | `k→v` | `k→v` |
| put `k→v₁` | put `k→v₂` | MMC | Real | Choose | re-execute winner's put | `k→v₂` | `k→v₁` |
| put `k→v₁` | remove `k` | MDC | Real | Choose | re-execute winner | `k` absent | `k→v₁` |
| remove `k` | put `k→v₂` | DMC | Real | Choose | re-execute winner | `k→v₂` | `k` absent |
| remove `k` | remove `k` | DDC | Pseudo | Ignore | — | absent | absent |

## List items (same element, same property, same item id)

Verified by `ConflictDetectionTests`, `StateSpaceOracleTests.ListExploration`,
`DeepStateSpaceExplorationTests` (list scenario, 601 states),
`RegressionConvergenceTests`.

| Parent (net) | Child (net) | Type | Severity | Policy | Resolution | Result ChildWins | Result ParentWins |
|---|---|---|---|---|---|---|---|
| insert/move `i@a`, value `v` | insert/move `i@a`, value `v` (same anchor & value) | MMC | Pseudo | Ignore | — | `i` after `a` with `v` | same |
| insert/move `i` (placement/value 1) | insert/move `i` (placement/value 2) | MMC | Real | Choose | re-execute winner's insert **+ its follower chain** | child's placement & value | parent's placement & value |
| insert/move `i` | remove `i` | MDC | Pseudo | Ignore | — | `i` removed (**delete wins**; tombstones make the pair commutative) | same |
| remove `i` | insert/move `i` | DMC | Pseudo | Ignore | — | `i` removed | same |
| remove `i` | remove `i` | DDC | Pseudo | Ignore | — | removed | removed |

## List anchors (order dependencies between *different* items)

Verified by `SyncScenarioTests` (thesis POC orders),
`SchoolScenarioTests.WaitingList…`, `DeepStateSpaceExplorationTests`,
`RegressionConvergenceTests.Repro1–8`. See
[list-synchronization.md](list-synchronization.md) for the algorithm.

| Situation | Type | Severity | Policy | Resolution | Result ChildWins | Result ParentWins |
|---|---|---|---|---|---|---|
| Both sides inserted **different items at the same anchor** `a` | MMC | Real | Choose | re-execute the winner side's whole **anchor-group closure** (its inserts at `a` + every insert transitively anchored behind them, in delta order) | child's sequence directly after `a`, parent's sequence behind it | parent's sequence directly after `a`, child's behind it |
| Child inserted after an item the parent **removed** (or mirrored) | DMC / MDC | Real | **Merge** | keep the dependent insert, **re-anchor** it to the closest alive predecessor of the tombstoned anchor (or the head), re-execute its follower chain | position-preserving re-anchor — strategy-independent | same |
| One side inserted after an item the other side **moved** | MMC | Real | **Merge** | re-execute the dependent insert + followers so they follow the anchor's new position | dependents follow the moved anchor — strategy-independent | same |

**Invariant across all list conflicts:** no insert is ever lost — the winner
strategy only decides the *interleaving order*; removed items stay removed
(delete wins); and surviving base items keep their relative order.

## Coverage

The mixed-cardinality state-space exploration reaches conflicts of every
category above and all four merge types (MMC, DMC, MDC, DDC) in both
severities, and asserts convergence — including tombstone state — from every
reachable state under both strategies; the randomized property tests re-check
the same invariants with three private workspaces and random interleavings.
