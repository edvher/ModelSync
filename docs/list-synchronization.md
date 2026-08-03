# List synchronization: conflicts and resolutions

This document explains how ModelSync keeps *ordered lists* convergent across
the star topology, under the one hard rule of the whole system: **a workspace
can only ever change by executing new incremental operations**. There is no
state upload, no "overwrite with my copy" — every fix, including conflict
resolution, must itself be an operation appended to a branch.

## The setting

- One **public workspace** `P` and any number of private workspaces. The
  operation history is a single growing **tree**: the main branch is `P`,
  every other branch is one private workspace.
- **Update** = pull: the private branch replays the public delta since the
  branching point (LCA), is re-attached onto the public head, and appends
  **resolution operations** for every non-commutative conflict.
- **Commit** = push (fast-forward only): the public branch replays the private
  delta — which is exactly *the private operations concatenated with their
  resolutions* — and the public head moves to the private head.
- A model **is** the replay of its branch: checking out any workspace fresh
  and replaying its path from the root must reproduce the live state
  operation by operation.

## Why lists are the hard part

Single values, sets and maps are *keyed* slots: the last writer of a key wins,
so one re-executed winner operation per contested key restores convergence.
Lists are different — an insert's meaning depends on its *position*, and
positions are expressed relative to other items that may themselves be
inserted, moved or removed concurrently.

## Representation: stable ids, anchors, tombstones

Three representation choices make deterministic list merging possible:

1. **Stable item ids.** Every list item gets an id at insert time that never
   changes, independent of value and position. Operations reference ids, not
   indexes — an index would silently point at a different item after a
   concurrent edit.
2. **Anchor-based inserts.** `InsertListItem(item, value, after)` places the
   item *after* an anchor item (`after = null` means the head). Executing an
   insert whose item already exists **relinks (moves)** it behind the anchor
   and re-asserts its value. Re-execution is therefore not a duplicate — it is
   a deterministic "put it back here", which is exactly what a resolution
   needs.
3. **Tombstones.** Removing an item only marks its node deleted; the node
   stays in the chain. Later operations anchored on the removed item stay
   executable (they land after the tombstone, i.e. at its old position), and a
   re-inserted removed item stays dead — **delete always wins** for list
   items, on every replica, in every replay order.

## Detection: net effects, then three questions

When a workspace updates, the public delta and the private delta since the
LCA are each indexed by their **net effect** — the last surviving operation
per element, per keyed slot, and per list item (the thesis rule
*Delete ≙ Modify\* Delete*: a branch is classified by where it ended up, not
by every intermediate step). Net inserts are additionally grouped by their
anchor. A removed item whose position still carries surviving followers (a
"carrier") keeps competing at its anchor so those followers aren't orphaned.

Detection then joins the two indexes and asks, in O(n + m):

| Question | Conflict | Class |
|---|---|---|
| Did both sides net-operate on the **same item**? | insert/move vs. insert/move: same anchor & value → pseudo; otherwise a real binary choice. Insert vs. remove → pseudo (delete wins, commutative under tombstones). Remove vs. remove → pseudo. | `ListOrder` |
| Did both sides net-insert **different items at the same anchor**? | The interleaving is ambiguous — a real choice between "my sequence first" and "yours first". | `ListOrder` |
| Did one side insert **after an item the other side removed or moved**? | The dependent insert must follow its anchor's fate; no winner to pick, but a merge action is required. | `ListAnchorDeleted` / `ListAnchorMoved` |

## Resolution: re-execute the winner's closure

A resolution is never a diff of states — it is a set of **new operations**
(clones of existing ones, marked as resolutions) appended to the updating
branch. Because inserting an existing item *moves* it, re-executing an insert
sequence deterministically re-asserts that sequence's order on any replica,
regardless of what happened in between. Concretely:

- **Same anchor, competing inserts** — take the winner side (child or parent
  by strategy) and re-execute its whole **anchor group closure**: the winner's
  inserts at that anchor *plus every insert transitively anchored behind
  them*, in the winner delta's original order. Re-executing the closure and
  not just one operation is what keeps whole inserted sequences (`U, V`
  behind `X`) intact — this reproduces the thesis POC outcomes
  `[A, X, U, V, B, C]` (parent wins) and `[A, U, V, X, B, C]` (child wins).
- **Anchor deleted** — the dependent insert is kept, re-anchored onto the
  **closest alive predecessor** of the tombstoned anchor (or the head), and
  its follower chain is re-executed behind it. Position is preserved as
  closely as the surviving items allow.
- **Anchor moved** — the dependent insert and its followers are re-executed
  unchanged; since the anchor has a new position, re-execution makes the
  dependents follow it there.
- **Same item, placed differently** — re-execute the winning insert (with its
  followers); the losing placement is overridden because re-execution moves
  the item.
- **Insert vs. remove of the same item** — nothing to append: tombstones make
  the pair commutative and the removal wins by construction.

## Why every replica converges

The updating workspace's branch, after update, reads:

```
public history · re-executed local delta · resolutions
```

and this concatenation is *the* canonical order everywhere:

- the **updating workspace's live model** applies the public delta, then
  re-executes its own delta, then applies the resolutions — so its state
  equals its branch replay exactly;
- on **commit**, the public workspace executes the same local delta and the
  same resolutions on top of the same public history;
- every **other workspace** receives that history through its own later
  update;
- a **fresh checkout** replays the branch literally.

Same operations, same order, deterministic execution — same state. The
re-execution step is essential: the updating workspace executed its own edits
*before* it saw the public delta, but the branch says they come *after*.
Re-executing them (a no-op for commutative edits, a deterministic re-assertion
otherwise) realigns the live model with the replay order; the resolutions then
have the final word on every contested slot. This invariant is exercised
exhaustively by `DeepStateSpaceExplorationTests`, which from every reachable
state of a bounded list/star configuration (every insert position, every
removal, every commit/update interleaving, both winner strategies) asserts
that the public model, all private models and a fresh replay are identical.
