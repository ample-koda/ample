> Status (2026-10-02): design sketch from a brainstorm. Nothing here is built or tested. [History v1](history.md) holds the decisions (lines, studio and garage, promote, tags, checkpoints, drop and cleanup); this page works them out into a data model, operations, screens and scenarios. The clickable sketch in `sketch/` shows the screens.

# History management (detail)

## 1. Principles
1. **Nothing is lost silently.** Every move is a pointer move recorded in the operation log; only `cleanup` and `purge` remove data, and both are dangerous acts with a dry run, a backup suggestion and typed acceptance.
2. **Everything stays reachable** until it is deliberately removed: any revision, any line (also archived ones), any earlier arrangement of the lines.
3. **Revisions are immutable** and tamper-evident (their id is a hash of content and parent).
4. **No merges in v1.** Lines diverge; one is promoted. "Adopt this block" and structure-aware merge are tracked in [Future ideas](future.md).
5. **Natural navigation is by line, leaf, checkpoint and tag**; hashes are the exception.
6. **Two audiences, one model:** a simple view that shows what matters, an advanced view that shows everything.

## 2. Data model (SQLite in `.ample/history.db`)
```
revision   id (hash of content+parent), parent, line_at_creation, author, time, message,
           kind (save | auto | adopt | migration), content_ref, summary_json
content    id (hash), sources_tree_hash, lock_hash, settings_hash, cuts_json, baked_refs_json, modules_json
line       name, tip, forked_from (revision), role (studio | garage | fork), state (live | archived), created, renamed_from
tag        name, target, type (normal | release | frozen), created_by, created_at          + tag_history(name, target, op, by, at, reason)
checkpoint name, target, auto (0|1), created_at
arrangement id, taken_at, studio_tip, lines_json   one row per promote and fork: where every line stood (so "restore the tree as it was" works)
operation  id, time, by, kind, args_json, before_json, after_json, danger (0..3), reason     the operation log (undo and redo walk this)
tombstone  op_id, scope, what_was_removed_json, size, backup_ref                              what cleanup/purge removed
release    id, tag, revision, sealed_hash, kind (recipe-exact | data-exact), lineage_json
```
- A **revision id** is `hash(parent id + content id + author + time + message)`, so history cannot be rewritten without changing ids; with the `signing` module the id is also signed.
- **Content** is separate from the revision so identical states share rows; sources are stored as a tree of file hashes (a snapshot), not as diffs.
- **Takes and baked data** are not in the database; revisions reference them by hash (take bank, see [Bake](bake.md)).
- **Secrets are never in history;** a revision records only which credential name and version it needed (see section 9).

## 3. What a revision contains
| In the revision | Not in the revision |
| --- | --- |
| Sources (`.amplet`, lyrics, MIDI, chords, packs list), `ample.lock`, cuts, which baked blocks are used, the enabled modules, project settings that affect results, the layout of the block graph | Credentials, machine-local settings, the cache and scratch, the take bank files themselves (referenced by hash), unsaved drafts |
**Layout-only changes** (moving a block on the canvas) are saved but marked `layout` in the summary, so they do not look like musical changes in the history and can be hidden by a filter.

## 4. Operations (what each does, how risky)
| Operation | Effect on pointers | Undo | Danger |
| --- | --- | --- | --- |
| `save` | new revision on the line you are on (forks by itself if you are not on a tip) | undo | 0 |
| `checkpoint` | adds a light label (renamable, movable, removable) | undo | 0 |
| `tag` | adds a locked label | undo | 0 |
| `go` | moves **here**; if the target studio differs from now, asks "replace studio too?" (default no) | undo | 0 |
| `go --peek` | read-only checkout; rebuilds go to `scratch/` | leave | 0 |
| `fork` | new line from a revision | undo | 0 |
| `promote` | moves **studio**; old tip kept as `archive/studio-N`; no merge | undo | 1 |
| `drop` | marks a leaf archived | unarchive / undo | 1 |
| `rename line` | renames, keeps `renamed_from` | undo | 1 |
| `re-tag` / `unlock tag` | moves a locked tag; old target in `tag_history` | undo | 2 (typed name and reason) |
| `cleanup` | removes archived leaves and unreferenced data for real | **no** (only from a backup) | 2 (dry run, backup suggestion, typed scope, tombstone) |
| `purge` | removes chosen revisions or credential versions for real | **no** | 3 (like cleanup, stronger for anything holding a sealed release) |
| `undo` / `redo` | steps through the operation log (does not undo cleanup/purge) | redo | 0 |
**Unsaved work** is never a revision. When you move away from unsaved changes, AMPLE takes an automatic checkpoint (a draft revision of kind `auto`, hidden by default) first, so nothing is lost; a crash leaves a draft that is offered back on the next open.

## 5. The History screen (anatomy)
```
┌ toolbar: [Save] [Checkpoint] [Promote] [Fork]  search [___________]  filters ▾   view: (•) Simple ( ) Advanced   [Compare] [Export] [Cleanup]
├ graph (lanes: studio, garage, forks)         ┬ detail of the selected revision
│  ★ = studio   ◯ = here   ⚑ checkpoint   🏷 tag  │  message, author, time, line, tags, checkpoints
│  faded = archived                             │  changed blocks, layout-only flag, takes used
│                                               │  buttons: Go · Look only · Make studio · Fork · Checkpoint · Tag · Compare
├ leaves list (ample leaves)                    ┴
└ status line: here r12 (garage, tip) · studio r9 · 3 unsaved changes · last backup 2 days ago
```
- **Simple view:** the recent revisions plus everything that matters (tips, studio, here, tags, user checkpoints), compressed with "... 14 older" markers. Archived lines and automatic checkpoints are hidden.
- **Advanced view:** every revision, archived lines (faded), automatic checkpoints, ids and parent links, tools and baked counts.
- **Filters:** text (message, block name), author, line, "touched block ...", tag or checkpoint only, date range, kind (save, auto), layout-only on/off.
- **Keyboard:** arrows move along a line and between lines at forks, Enter = go, Space = look only, P = promote, F = fork, C = checkpoint, T = tag, / = search.
- **Everywhere else:** the header always shows the line you are on and the studio revision; the Structure browser (left) is the same data as a tree.

### The branch tree and the "branch laid out" view
- **Structure panel (left), simple view:** only the **path from the root to the branch you are on** plus its **direct leaves** (branches forked from it), and a **+ New leaf under <branch>** button that forks from where you are. Recent revisions of the active branch sit in a collapsed list. This replaces long revision lists in the tree.
- **Structure panel, advanced view:** the whole branch tree (parent to children), archived branches faded.
- **Clicking a branch name** opens the **Branches** tab: one row laid out left to right: the root (`studio`) on the left, an **arrow** to each branch on the path, the focused branch highlighted as **THIS BRANCH**, and its direct leaves stacked on the right. The row scrolls sideways and **starts centred on the focused branch**. Each branch is a box with its tip message, where it forked from, a strip of revision dots (the dot where the next branch forks is outlined, the dot you are on is ringed) and buttons Go, Look, + Leaf, Drop or Unarchive. Clicking another box refocuses the row.
- **Branch manager (dedicated view):** a switch above the canvas replaces the block view with the **whole hierarchy** as a scrollable tree (root left, siblings stacked, any number of leaves per branch and leaves of leaves). Focusing a branch there also centres it in the Branches tab; you can fold subtrees, search, show archived, rename, go, look, add a leaf and drop. The Branches tab under the canvas deliberately shows only the direct line.
- **Parent of a branch** is derived, not stored: the line of the revision its first revision was forked from. Promote only moves `studio`; it does not change who is whose parent.

## 6. Compare
Pick two revisions (or a revision and "now"). The result is **structural**, not a text diff:
- **Blocks:** added, removed, renamed (detected by stable block id), moved (layout only), changed.
- **Parameters:** old to new per block, with the node seed unchanged or changed.
- **Links:** added and removed connections.
- **Takes:** same take hash, different take hash, or baked versus recipe. A changed dynamic block offers **Listen A/B** (plays the two takes, aligned).
- **Text:** the `.amplet` diff as a secondary tab.
- **Verdict line:** "audio probably changed in: chorus_vox, mixer, master" (derived from which blocks and their downstream changed), "nothing audible changed: layout only".

## 7. Search
An index over messages, authors, touched block ids and parameter names (kept in SQLite). Queries like "where did chorus_vox's seed change", "all revisions by alex in October", "everything tagged release". Results open in the graph with the matches highlighted.

## 8. Going back, forking and restoring the tree (worked scenarios)
1. **Go back 5, save, then 1 back and 5 up.** `go ~5` moves here. `save` from a non-tip forks a new line automatically (named from the fork point). `go ~1` then `go +5` follow the path you walked (like a browser's back and forward); at a fork the arrow keys choose a line.
2. **Promote.** garage tip r12 becomes studio. The old studio tip r9 is kept as `archive/studio-r9`. A second garage forked from the new studio and promoted later makes the first garage unreachable in the daily view (still in the graph through "show archived" and in advanced).
3. **Restore the tree as it was.** `go r7` asks: "Should studio be replaced too?" **No (default):** only garage (and other children of studio) come back, as children of the current studio. **Yes:** studio also moves back to what it was at r7, the old studio goes to the archive, `undo` reverses it. This uses the `arrangement` rows.
4. **Experiment that went nowhere.** `drop` the leaf (archived, undoable). Weeks later `cleanup` lists archived leaves with their size and impact; you choose which to remove for real, after the backup suggestion.
5. **Rebuild an old release.** `go v1.0 --peek` opens it read-only; Build runs into `scratch/`; compare the rebuilt hash with the sealed release; leave and everything is as before.

## 9. Credentials have their own timeline
Credentials are not part of revisions. The vault has its own numbered timeline (added, rotated, revoked, by whom) with sealed values. A revision records *which* credential and version it needed; `verify` reports "needs weather_api v2, current is v3". Going back in the project never restores or exposes an old secret. Old credential versions can be purged on their own (dangerous act). See [Backup](backup.md) for how the vault travels.

## 10. Releases in the history
A release is a **locked tag** plus a **sealed release record** (hash, recipe-exact or data-exact, lineage `based on` / `remix of`). It can sit on any line, recommended on `studio`. The history screen marks releases, and the lineage view shows how releases derive from each other (also across projects, by uuid). A sealed release's tag cannot be force-moved; a new revision means a new release and a new tag. Tag protection layers are in [History v1](history.md).

## 11. Storage and size
- Revisions are small (hashes and JSON); the size is the **take bank** and **baked data**.
- `ample size` reports by line and by revision: how much each line uniquely holds, so cleanup choices are informed ("verse-redo holds 1.2 GB that nothing else uses").
- Cleanup candidates: archived leaves, takes referenced by no revision, tag or checkpoint, old `auto` checkpoints beyond a policy (manual for now), scratch and cache.
- Never a candidate without extra confirmation: anything a sealed release, a tag or a user checkpoint refers to.

## 12. Integrity
- Single writer per project (instance lock); SQLite WAL; every operation is one transaction.
- `ample verify-history` recomputes revision ids and checks parents; `ample doctor` includes it (quick: recent revisions; full: all).
- With `signing`, tags and releases are signed; a move made outside AMPLE (editing the database) shows up as "not set by the owner".
- A rolling daily copy of `history.db` protects against corruption.

## 13. Export and mirror
- **Export a slice** (`ample export --scope line:garage` or `tag:v1.0` or `checkpoint:"chorus works"`) as a `.ample` file with chosen contents; opening it creates a new project or, later, imports the line into an existing one (see [Future ideas](future.md): pull and push).
- **Git mirror** (module): one-way mirror of lines and tags with the release hash in annotated tags; conversions both ways keep raw git objects for a lossless round trip.

## 14. CLI twins
```
ample log [--all] [--tree]        ample leaves [--all]        ample go <line|leaf|~N|+N|checkpoint|tag|hash> [--peek] [--with-studio]
ample save -m "..."               ample checkpoint ["name"]   ample checkpoints      ample tag <name> [<rev>]   ample tag --history <name>
ample fork [<name>]               ample promote [<rev>]       ample drop <leaf>       ample unarchive <leaf>     ample cleanup
ample compare <a> <b>             ample undo / redo           ample ops               ample size              ample verify-history
ample export --scope <line|tag|checkpoint> --contents sources,baked,takes,history
```

## 15. Open questions
1. Should a **timeline scrubber** (drag through revisions with a quick audio preview of the master) be part of v1 or a later nicety?
2. How long are **auto drafts** kept, and when are they thinned (manual for now; propose a gentle default later)?
3. Do **layout-only** changes get their own revisions, or are they folded into the next real change?
4. Should compare show **sound** differences measured on the rendered takes (loudness, spectral change) next to the structural ones?
5. Is the **operation log** shown to the user as a full panel, or only through undo and redo and the Log tab?
