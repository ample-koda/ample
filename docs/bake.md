> Status (2026-10-02): design sketch from a brainstorm. Nothing here is built or tested.

# Bake: dynamic results that become data

A **dynamic node** is anything whose result is not a pure function of the text: an AI voice, a weather lookup, a "make master" step, an analysis of an imported file. Normally AMPLE records its recipe (inputs, seeds, toolprint) and can render it again. **Baking** turns a node's result into a **data block** that is part of the project and of every backup.

## Why
- The recipe may stop working (a tool disappears, a model is updated, a web source changes, a different machine gives a different take; this was measured with ACE-Step).
- The result is what you liked. The recipe is only the way you got it.

## What a baked block is
- The result bytes plus metadata: hash, size, which node and recipe made it, the toolprint and platform, the time, who baked it.
- Stored in the **take bank / data store** (hash-addressed, immutable) and **included in backups and exports** (according to profile; a partial export says when baked data is missing).
- Referenced by hash from the node in `ample.lock`; the project then renders from the data instead of calling the tool.

```
voice chorus_vox  using anna  { lyrics chorus }
  bake sha256:MOCK...         # data-exact from now on; the recipe is kept as provenance
```

## Recipe-exact versus data-exact
A release states which one it is:
- **Recipe-exact:** the recipe plus pinned tools reproduces the same bytes on a declared platform class; no baked data is needed (but may exist).
- **Data-exact:** the result rests on baked data. Reproducible by definition, because the data is the project, but the recipe may no longer work.
`ample rederive <node>` re-runs the recipe and compares: if the hash matches, the node is proven recipe-exact and says so; if not, the difference is shown and the baked data stays authoritative.

## Commands (sketch)
```
ample bake chorus_vox            # freeze the current result of this node into data
ample bake --all-dynamic         # every dynamic node of the current cut
ample unbake chorus_vox          # back to recipe only (the data stays in the bank until cleanup)
ample rederive chorus_vox        # recipe-exact test (needs the tools)
ample bake --list                # what is baked, size, recipe-exact status
```
In the UI: right-click a block, "Bake" / "Reset recorded values"; baked blocks show a small lock-and-disk marker.

## Interaction with other parts
- **Variants:** picking a variant locks it by take hash; baking is the same idea for any dynamic node, and a pick is a bake of that variant.
- **History:** baked blocks come back with a revision. A baked block is cleaned up (`ample gc`) only when no revision, tag, checkpoint or archive refers to it; the real removal is a purge (dangerous act, impact shown, backup suggested).
- **External data** (weather, remote sources): `lock` records it once, `refresh` fetches again on purpose; a baked block is a lock with provenance and a hash.
- **Storage:** big baked audio may live outside git (LFS or AMPLE's own store) with the usual warning that can be silenced inline (`accept external until ...`).
- **Backups:** profile `minimal` leaves baked data out (and says what that costs), `standard` and `complete` include it.

## Open details
- Granularity: bake a node, a segment, a whole cut?
- Whether a bake should warn when the tool that made it is no longer present (suggested: show it in `verify --present`, not as an error).
- Compression/deduplication of baked audio in the store.
