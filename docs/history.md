> Status (2026-10-02): design sketch from a brainstorm. Nothing here is built or tested.

# History v1: branches and promote (mock)

Keep it simple for now: you can branch, but you never merge content. You **promote** one branch to be the main line and continue from there.

## Model
- Revisions form a graph with `parents[]` (so multiple parents are *representable*, e.g. when importing from git), but the tool only creates single-parent revisions.
- `main` is the main line. Other branches are experiments.
- **Promote** = choose: `main` now points at the branch tip. No content is merged, so there are no conflicts.
- The old `main` tip is kept as `archive/main-<date>` (read-only, can be branched from again). Nothing is deleted.
- **Sealed releases stay valid forever**, also those made on an archived line; `ample lineage` shows them under the archive.
- Every promote writes an entry in the hash-chained ledger: which branch, which old tip, who, when.

## Commands
````
ample branch <name>                  # new branch from the current revision
ample branches                       # list with revisions ahead of main
ample promote <name> --dry-run       # show what changes and what drops off main
ample promote <name>                 # main -> <name>, old main tip archived
ample branch --from archive/<name> <new>   # continue an archived line
````

## Rules for promote
- Clean working tree; static checks (syntax, cycles, hash references) pass on the target.
- If `main` has moved on since the branch point, the dry run lists the revisions and releases that drop off main (they stay in the archive).
- Other branches stay as they are; promoting one of them later replaces `main` again. Never a merge.

## Git mirror
Promote moves `main` of the mirror; the old tip is kept as a hidden ref `refs/ample/archive/*`, so ordinary git clients do not see branches vanish. The mirror is derived data and may be force-updated.

## Listening A/B between branches (cheap, fits v1)
`ample ab <branch1> <branch2> --block chorus` previews the same block from both branches.

## Navigating history: go, promote, peek, undo
Every revision ever made stays visible and reachable; moving around never rewrites history, only pointers move.

**Two pointers**
- **here** (current): the revision you are on and working from.
- **the real deal** (`main`): the revision that counts as the official one.

Double-clicking any revision moves *here*. "Make this the real deal" (promote) moves `main`. They are independent, so you can look and work without changing what is official.

**Example.** Go 5 revisions back (double-click, or `ample go ~5`). Make a commit: because you are not on a tip, it starts a **new line** automatically (named after the fork point, renamable; there is no loose "detached HEAD" where work can get lost, every tip stays reachable). Then change your mind: "1 back from the original line, then 5 up" is another double-click, or `ample go ~1` followed by `ample go +5`. `+N` follows **the path you walked before** (like a browser's forward button), also through forks. In the graph: left/right along a line, up/down to switch line at a fork.

**The graph (like a git client):** all revisions including other lines and the archive (faded), with markers for *here*, *the real deal*, releases and bookmarks. Context menu: Go here · Look only · Make this the real deal · Fork here (name a line) · Compare with now or with the real deal · Listen A/B · Bookmark · Take just this block · Copy id. Search by block, message or release.

**Commands**
````
ample log                       # the graph: every revision, all lines (hashes shown here)
ample leaves                    # the branches/leaves from where you stand (the natural way to navigate)
ample go <line|leaf|~N|+N>      # move "here" by line name, leaf number or steps (--peek: read-only)
ample go <hash>                 # allowed, but the exception; normally you pick from `leaves`/`log`
ample promote <rev>             # move the real deal; old tip kept as archive/main-<date>
ample undo   ample redo         # step back and forth in the operation log
ample mark "before-the-remix"   # bookmark
ample purge <rev> --reason "…"  # real deletion: deliberate, logged, rare
````
`rewind` is simply `go` + `promote`; `restore` is not needed: a commit after `go` forks by itself.

**Operation log (like Jujutsu's):** every `go` and `promote` is recorded, so `undo` and `redo` always take you back to where you were.

**Safeguards**
- Moving with unsaved work makes an automatic checkpoint (`auto/...`), so nothing is lost. Automatic checkpoints are also made before `promote`, block updates and migrations.
- What comes back with a revision: songs and editable text sources (`.amplet`, lyrics, chords, `.txt`, `.md`, `.csv`, `.json`, `.seq` and `.ampleseq`), lock, cuts, baked data blocks and the modules enabled in the project. Takes stay in the shared take bank (immutable). Secrets live outside history and do not change.
- Sealed releases stay where they are; going back never un-releases. Lineage keeps showing them.
- A baked block is only cleaned up (`ample gc`) when no revision or archive refers to it.
- Tools from back then may be missing: `verify --present` warns ("acestep@b7ba6d9 not available; baked data is present").
- Revisions may also be addressed by a date (`@2026-10-05`) or relative (`@5d`), but the primary way is the revision itself.

## Credentials get their own timeline
Secrets (API keys, tokens, signing key, passphrases) are **not** part of the project history. They live in a separate, protected store with their **own timeline**: every change (added, rotated, revoked, who/when) is a numbered entry, but the values are sealed (master passphrase, optional recovery key). Consequences:
- Going back in the project history never brings back or exposes an old secret, and never overwrites the current one.
- A revision only records *which credential it needed* (name and version), not the value. `verify` can say "needs weather_api v2, current is v3".
- Credentials have their own `log`, `undo` (restore a rotated value) and expiry; a revoked value can be purged on its own.
- The credential timeline is included in a backup only as sealed data, see [Backup](backup.md).

## Purge is a dangerous act
Removing data that is no longer relevant (old takes, baked blocks, archived lines, old credential versions) must be possible, but it is the one operation in AMPLE that cannot be undone, so it is treated as dangerous:
- **Never implicit.** Nothing purges as a side effect of another command; `gc` only removes what nothing refers to and says so, anything else needs `purge`.
- **Explicit acceptance.** `ample purge` first shows a **dry run**: what is removed, how much space, which revisions, releases, lineage links and baked blocks lose their data, which releases would drop from data-exact to recipe-exact (or become unreproducible), and what stays untouched.
- **Backup suggestion.** Before anything is deleted it says: "This cannot be undone. Make a backup first?" and offers `ample backup` right there (profile suggested from what is affected). Skipping needs an explicit choice.
- **Acceptance by typing**, not a bare yes: the user types the scope (for example the number of revisions or the line name), or passes `--accept "<what>"` with a reason in scripts. A UI needs the same: a red dialog listing the impact, a backup button, and a confirm that stays disabled until the scope is typed.
- **Leaves a tombstone:** the operation log keeps who, when, what scope and the reason; revisions stay in the graph as "purged (data removed)" so lineage remains understandable. Sealed releases are never purged without separate, stronger confirmation.

## Navigating by leaves, not hashes
Hashes are poor handles for people, so `go` is aimed at **lines and leaves** (the tips you can actually continue from); a hash is accepted but is the exception. `ample leaves` shows, from where you are now, what is reachable and how far:

````
$ ample leaves
  here  ●  chorus-rework  (3 revisions after the split from main)
  [1]   ★  main           +2 ahead of the split   "mastered v2"            release: v2
  [2]      verse-redo     +4                      "new voice for verse"
  [3]      archive/main-2026-10-02  +1            (archived)               faded
  [4]      auto/before-promote      +0            (automatic checkpoint)
$ ample go 2        # or: ample go verse-redo
````
Each entry shows the line name, distance from here (back to the split, then forward), the last message, markers (the real deal, releases, archived, automatic) and a play/listen hint. `--all` includes archived and automatic ones; `--tree` draws the graph. The UI graph is the same view: leaves are the highlighted ends, double-click one to go there.

## Dropping a leaf: archive first, clean up later
Experiments that went nowhere are common, so dropping a dead-end leaf must be easy, but deleting stays dangerous. Two steps:

1. **`ample drop <leaf>` = mark as archived** (the usual, safe action). Nothing is deleted: the leaf (and the revisions only it contains) fade out of `leaves` and `log`, stay in `log --all` and the graph's archive filter, and `ample unarchive <leaf>` or `undo` brings them back. Revisions shared with another line are never affected; only the part reachable from no other line counts as the leaf's own.
2. **`ample cleanup` = the real removal, later, on purpose.** It targets archived leaves (never live ones) and is a purge: dry run with impact (revisions, space, baked blocks, releases that lose data), a backup suggestion, acceptance by typing the scope, a tombstone in the log. It can also be limited by age (`--archived-older-than 30d`) and shows which archived leaves it would take so single ones can be skipped.

Rules: the real deal and the revision you are on cannot be dropped; a leaf holding a sealed release is not dropped without a stronger confirmation; `drop` on a leaf with children offers to drop the whole branch (listing the children) or just that tip. Dropping a line with a name asks for that name.

````
$ ample drop 2            # verse-redo: marked archived (4 revisions), undo available
$ ample cleanup           # later: lists archived leaves, shows impact, suggests a backup, asks you to type the scope
````

## The name of the real deal, and tags
**Name of the main line.** It is called **`studio`** and a new project starts with `studio` > `garage` (see "Studio and garage" below). The names are defaults and are not meant to change; they can be renamed if someone really wants to. Importing from git maps `main`/`master` to `studio`.

**Tags are locked names for a revision.** Meant for releases ("v1.0", "radio-edit"), but any revision can be tagged.
- `ample tag v1.0 [<rev>]` creates a tag (default: here). A tag never moves by accident: tagging an existing name fails.
- `ample tag v1.0 <rev> --force --reason "..."` re-tags. It is allowed but dangerous-lite: the old target stays in the tag's own history (`ample tag --history v1.0`) and in the operation log, so a re-tag can be undone and nothing is lost silently.
- Removing a tag needs `--force` too and is logged; tag history is kept.
- `ample release` can tag automatically (`release v2` creates tag `v2` on the sealed revision). A sealed release's tag cannot be force-moved to a different revision; a different revision means a new release and a new tag.
- A tagged revision is protected: `drop` warns and `cleanup` skips it unless the tag is removed first on purpose.
- Tags show in `leaves`, `log` and the graph as labels; `go v1.0` works like any other target. Git mirror: tags map to git tags (annotated, with the hash of the release).

## Checkpoints: a quick lookup table of named moments
A **checkpoint** is a light, named pointer to a revision, a quick way to find a moment again ("before the remix", "chorus works"). It replaces the earlier `mark` bookmark.
- `ample checkpoint ["name"]` (UI: a button next to save). Without a name the default is a timestamp, e.g. `cp-2026-10-02-1415`. It records the revision you are on, and makes one first if there is unsaved work.
- **Unlike tags, checkpoints are not locked:** they can be renamed, moved and removed freely (removing one deletes only the label, never the revision). That is what makes them cheap.
- **Tags stay for what must not move** (releases, see above). A checkpoint can be promoted to a tag: `ample tag v1.0 --from-checkpoint "chorus works"`.
- `ample checkpoints` lists them (name, time, line, message), newest first, searchable; `ample go "chorus works"` jumps to one, and the graph shows them as small labels with a filter. They count as markers in `leaves`.
- **Automatic ones** (`auto/...`, made before moves, promote, updates, migrations) are checkpoints too, but kept apart: hidden by default, grouped, and thinned out after a while (never the ones a revision or tag points at).
- A checkpoint protects nothing from `cleanup` by itself (it is only a label); a dropped leaf's checkpoints show as archived with it. Tags do protect.

## Protecting tags from tampering
Principle: moving a tag must be harder than making a revision, and never silent. Layers (1 to 4 default, 5 and 6 opt-in):
1. **No silent overwrite:** an existing tag name cannot be reused by plain `tag`.
2. **Typing to re-tag:** `--force` alone is not enough; the user types the tag name and a mandatory reason after seeing an impact summary (old and new target, whether a release or sealed hash hangs on it, who set it before). Same pattern as purge. Scripts need `--accept "<tag>" --reason "..."`; the UI has no shortcut.
3. **Tag types:** *release tags* are created by `release` and can never move (a different revision means a new release and a new tag); *normal tags* can be re-tagged under layer 2; a tag can be **frozen** (`ample tag --lock`) so only `unlock` (same confirmation) releases it.
4. **Tag history:** every create, move and delete is in the operation log (who, when, why) and reversible with `undo`. Backups and the git mirror carry the tag history.
5. **Signed tags (module `signing`, opt-in):** a tag signed with the author's key; `verify` reports a move made without that key ("not set by the owner"), which also catches edits made outside AMPLE.
6. **Tags inside the sealed release:** a release records its tag names and target hashes; `verify` flags a tag that no longer points at the sealed revision, also in a copy of the project elsewhere.

## Studio and garage
- A new project has two lines: **`studio`** (the top line, the real version) and below it **`garage`**, where you work; saves land on `garage`.
- **Promote:** `garage` promoted to `studio` sets `studio` to that revision. The old `studio` tip stays in history.
- **Fork from `studio`:** after a promote you can fork `studio` again, giving a new `garage`, and promote that one later too.
- **No merge:** when the second `garage` is promoted, the first `garage` is no longer reachable in the daily view (only through commit history), the same rule as archived leaves.
- **Going back to a commit rebuilds the tree as it was.** Every promote and fork records where each line stood (a snapshot of the pointers, kept with the operation log). `ample go <rev>` then asks: **"Should `studio` be replaced too?"**
  - **No (default):** only `garage` (and any other children of `studio`) is restored, as a child of the current `studio`. `studio` is not overwritten.
  - **Yes:** `studio` also returns to its state back then; the old top is kept as an archive and `undo` reverses it.
- **Releases are tags** (locked, see above) on **any** line, recommended from `studio`. There is no separate top level for releases.

## Temporary read-only checkout (look, rebuild, leave)
Any revision, tag or release can be opened **temporarily and read-only**, for example to rebuild an older release. This is `ample go <rev|tag> --peek` (UI: "Open read-only"); it is a mode, not a move of `here`.
- **Nothing in history can change:** saves, checkpoints and promotes are refused while peeking. The banner says what you are on ("read-only: v1.0 (tag), 2026-09-12").
- **Rebuild is allowed:** `render`/`master` and `verify --repro` run on the checked-out sources, lock and baked data. Results go to a **scratch area** (`.ample/scratch/<rev>/`), never into the project's takes or releases, so the sealed release stays untouched. Compare the rebuilt hash with the sealed one: same hash = reproduced; different = reported with the toolprint differences (a tool or model from back then may be missing: `verify --present` says what).
- **Leaving is free:** `ample go --back` (or closing the peek) returns to where you were, with your unsaved work exactly as it was. The scratch area is removed on leave, or kept on request (`--keep-scratch`) and listed for `cleanup`.
- **Turning it into work:** "work from here" (`ample go <rev>` without `--peek`) forks as described above. A rebuild you want to keep can be saved as a new take or release only from a normal (non-peek) state.

## Clean-up keeps a safety copy of dropped lines (2026-10-20)

Dropping a line is reversible, and so is the clean-up that follows it: before the clean-up removes the content of a dropped line it copies it to `.ample/history-archive/<timestamp>/` (`manifest.json` with line name, revision ids and files, plus `blobs/<hash>` with the exact bytes). The folder is built as `<timestamp>.partial`, every blob is read back and hashed, and only then is it renamed. If writing or verifying fails the clean-up removes nothing and stops with AMP5247. The dry run lists the dropped lines that will be archived and the size of the archive. **Bring back** puts the content of a dropped line back from the archive when the live copy is gone (without an archive it still fails cleanly with AMP5246). Nothing in AMPLE deletes the archive; the storage clean-up does not scan or remove the folder, but it counts the archived revisions as references so the takes they name stay protected.
