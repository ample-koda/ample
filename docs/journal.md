# AMPLE Redux: the durable recovery journal

> **Status (2026-10-18):** AR record references, durable sequence allocation, bounded read-only search (including the Codex MCP tool), redacted assistant-task archives, opt-in session conversation capture, independent browser recovery drafts, project history preview and explicit retention controls are implemented. The History tab also exposes the open song or nested module's browser undo/redo states: preview is read-only, double-click confirms restore, Ctrl-double-click restores directly, and AR badges appear only for an exact text hash found in a durable pre-image record. Checkpoint preflights all source drafts and saves every matching dirty song/module work buffer before creating one snapshot. Existing projects continue using `.ample/blackbox/` on disk for compatibility.

## The journal and the Always rule

## 1. The idea
The history (lines, revisions, tags) holds the moments the producer chose to keep. **AMPLE Redux** is the append-only recovery journal underneath it. It records durable project changes and assistant-task artifacts so work can be inspected and recovered after a crash, a bad replace, a deleted block, a seed overwritten or an undo that went too far.

It is the second half of the **Always rule** (Decisions): the Always rule says a take never exists without its plan; AMPLE Redux says *no change exists without a trace*.

## 2. How it differs from the other layers
| Layer | Who decides | Granularity | Lives | Shown |
| --- | --- | --- | --- | --- |
| Editor undo/redo | automatic, per open song or nested module | one edit step; text and undo/redo stack are recoverable from this browser | browser recovery buffer, offered when that file is opened after restart; the recovery diff can preselect restore or saved version for other files | yes: Undo/Redo controls and the Edit history timeline |
| Automatic checkpoints (`auto/...`) | automatic, before risky moves | one per move, promote, update, migration | in the history | hidden by default |
| **History** (lines, revisions, tags, checkpoints) | the producer | one per Save | the project, kept forever (until cleanup or purge) | yes |
| **AMPLE Redux** | automatic, always | each durable record has an AR reference | `.ample/blackbox/` (compatibility path) | recovery and read-only inspection |

## AR references (implemented)

Every journal record has a monotonic sequence and two representations: the canonical, copyable form `AR-0000000000000001` and the compact UI form `AR-1`. IDs contain no date or suffix; timestamps are stored separately in UTC. The sequence high-water mark is persisted before appending a record, so a failed append may leave a gap but cannot make that number available again. The maximum sequence is 9,999,999,999,999,999. AMPLE's project writer lock remains the single-writer boundary; multiple read-only viewers can inspect the same archive.

## 3. What is recorded
Everything that changes the working copy or the project:
- **Text and structure:** edits to `.amplet` and the other text files, block additions, deletions and moves, parameter changes, links, groups, the layout.
- **Seeds, freeze levels and plans:** every seed change (with the old value), freezing and unfreezing, picked variants.
- **Time and placement:** arrangement edits.
- **Library:** replace, rename, relocate, copy, remove (with the full before state).
- **Settings:** project, plugin and (non-secret) module settings.
- **Renders:** every build start and finish, which take hash it made, and its plan (already stored by the Always rule).
- **Deletions:** the full content that was deleted, not only the fact that it was.
- **History operations:** go, promote, fork, drop, cleanup, tag and checkpoint moves (they are in the operation log too; the journal keeps the same facts in time order with everything else).
**Never recorded:** secret values (only the name and version of a credential, as everywhere else), transient keystrokes before save, and audio (takes live in the take bank; the journal only holds their hashes). The Edit history timeline can therefore show an AR badge only when the whole file-text state exactly matches a durable song or module pre-image stored in Redux; the lookup pages through the journal and an unrecorded browser-only step has no AR badge.

## 4. Format
- **Append-only** entries in segment files (or a table): id, time, who, session, **group id** (one user action, such as "replace kit/tape by kit/808", is one group), kind, target (block id, file, setting), **before and after** (a small patch, or a content hash for bigger things), and a hash of the previous entry (a chain, so tampering and gaps are detectable).
- **Crash-safe:** each entry is flushed before the change is reported done; a torn last entry is detected and ignored on the next start.
- **Snapshots:** every N minutes or K entries a full snapshot of the working copy (a cheap one: file hashes and the text), so restoring a moment does not mean replaying everything from the beginning.
- **Small:** text patches are tiny; the big things (takes, imports, baked data) are referenced by hash. A heavy week is expected to stay in the tens of megabytes (an estimate to measure, not a measurement).

## 4b. A separate store inside `.ample/`, built to survive when things go wrong
AMPLE Redux is **its own store**, not a table in `history.db`: it currently uses the compatible storage path **`.ample/blackbox/`**. If the history database is corrupted, AMPLE Redux is not; if Redux has a problem, the history is not. Bias: **better too much than too little.**
```
.ample/blackbox/
  README.txt            how to read this folder without AMPLE (plain text, always rewritten on open)
  MANIFEST              segment list, sizes, hashes and the head of the chain; rewritten atomically, MANIFEST.prev kept
  current.log           the open segment (append-only, flushed before a change counts as done)
  seg-000123.log.zst    sealed segments (compressed, checksummed, never edited again)
  snapshots/            periodic full copies of the small important files (song, lyrics, lock, project.json, links, assets, plugins.lock)
  db-backups/           a rolling daily copy of history.db (7 kept)
  damaged/              anything that failed a check is moved here, never deleted
```
- **Self-describing and simple:** JSON lines, each entry framed with a magic marker, a length and a **CRC**, plus the hash of the previous entry. A person (or a 50-line script) can read it; a damaged entry can be skipped because the next marker resynchronises the reader.
- **Write-ahead, fail closed for dangerous changes:** the entry is written and flushed **before** the change is applied. If the write fails (disk full, permissions), **destructive changes are refused** (delete, replace, purge, cleanup, migrate) with a clear message; trivial edits continue but a persistent red notice says AMPLE Redux is not recording.
- **Capture generously:** both **before and after** values (not only patches) for anything small; the raw **command and its inputs**, so a later bug in AMPLE cannot hide what was asked; a **snapshot every 5 minutes while working**, and always **before** a dangerous act, an upgrade or a migration; the **content of everything deleted**.
- **A second copy:** sealed segments and snapshots are also copied to a mirror **outside the project folder** (`<Home>/blackbox-mirror/<project uuid>/`), so deleting or overwriting the project folder does not take AMPLE Redux with it. On by default, can be turned off, and says where it is.
- **Integrity checks:** on open the tail is verified (a torn last entry is cut off, the bytes kept in `damaged/`); `ample blackbox verify` checks every CRC, the whole hash chain and the manifest; `ample blackbox salvage` reads what can be read past a damaged part.
- **Not tied to the clock:** each entry has a sequence number, the wall-clock time and a monotonic time, so a clock jump or a time-zone change cannot reorder history.
- **One writer, many readers:** a single writer per project (the instance lock); recovery tools and scripts read without a lock.
- **Permissions:** owner-only files. **Not encrypted by default**; optional encryption at rest (records and mirror sealed with AES-256-GCM) is built, see [Security](security.md). Never put `.ample/blackbox/` in a cloud-synced folder (the health check warns).
- **Protected by AMPLE itself:** nothing inside its retention window is deleted by AMPLE; "Delete project" lists its size in the dry run; a restore from a `complete` backup brings it back.

### What can go wrong, and what saves you
| Situation | What happens |
| --- | --- |
| Crash or power loss while writing | the torn last entry is detected and set aside; everything before it is intact; unsaved changes are offered back |
| Disk full | destructive changes are refused; red notice for the rest; nothing is silently lost |
| `history.db` corrupted | AMPLE Redux is separate; restore a daily db copy from `db-backups/` or rebuild the working copy from snapshots plus entries |
| Project folder deleted or overwritten | the mirror in Home has the segments and snapshots |
| A bug in a new AMPLE version corrupts data | the raw commands and before-values are in AMPLE Redux; the snapshot taken before the upgrade is the way back |
| `.ample/blackbox/` deleted by accident | the mirror; and the health check shows "AMPLE Redux archive missing" at the next open |
| Cloud-sync conflict copies | detected by the manifest chain; the health check warns and keeps both copies |
| Clock jump | sequence numbers keep the order; the jump itself is recorded |
| Power loss while sealing a segment | sealing is write-to-temp then rename; the open segment stays valid until the rename succeeds |

## 5. Recovery (the only place it shows)
Hidden from the normal screens; reachable from **Health, AMPLE Redux (advanced)** and `ample recover`:
- **Timeline** of groups ("12:41 replaced kit/tape by kit/808", "12:43 deleted block bass2", "12:47 seed of chorus_vox changed") with **search** ("everything that touched chorus_vox's seed", "everything deleted today") and filters by block, kind and time.
- **Restore a part, not the world:** a block, a file, a parameter, a seed, a placement or a setting **as it was at a chosen moment**, into the working copy as an unsaved change (so it can be reviewed, saved or undone), or as a **new line** from that state.
- **Preview** what a moment looked like (read-only, like peek) and **compare** it with now.
- **Automatic nudges:** after a crash ("recovered N unsaved changes"), after a mass change ("12 blocks removed: recoverable from AMPLE Redux for 90 days") and when a dangerous act finishes, so the producer knows the net is there without having to think about it.
- The journal is **read-only history of the working copy**: recovering never edits it, it only adds new entries.

## 6. Retention and cleanup (later, deliberately)
A policy, shown once in the project settings and the Health panel, adjustable:
| Age | Kept |
| --- | --- |
| first 90 days (default) | every entry, and never thinned during the last 7 days |
| 90 days to 1 year | one snapshot per hour plus every entry of a **group that deleted or replaced something** (their content is kept the longest) |
| older than 1 year | only what a revision, tag, checkpoint or release refers to, and the deletions' content |
Also a **size budget** (default 10 GB, adjustable): a warning at 20% (the same threshold in the status and in the compaction result), and only then is the oldest detail thinned. The defaults err on the side of keeping too much.
- **Takes are pinned by the journal.** A take or baked block that the journal still refers to is not removed by `gc` or `cleanup` until its journal entries expire. That also rescues the variant you liked but did not pick.
- **Cleaning it up is a dangerous act** when done by hand (`ample journal prune --older-than 14d`): dry run, impact, backup suggestion, typed acceptance, tombstone. The automatic thinning follows the policy and is logged.
- **Backups and exports:** the journal is only in the `complete` backup profile and never in a partial export by default (it contains deleted content). A restore says whether a journal came along.

## 7. Privacy and safety
- The journal holds **deleted text** (lyrics, notes) and old settings. It is local, in `.ample/`, never sent anywhere, excluded from sharing, and part of the "what would be lost" dry run when a project is deleted.
- Secrets never enter it (see [Project management](project-management.md)); a credential change appears as "credential X rotated to version 3".
- Because it is hidden, the first run **tells the producer once** that it exists, what it keeps and for how long, and where to turn it off or shorten it.

## 8. Testing
- **Replay test:** replaying the journal from a snapshot must reproduce the working copy exactly, at random moments.
- **Crash test:** kill the process at random points while changing things; the journal and the project must agree after restart.
- **Fuzz:** random sequences of edits, deletions and replaces, with random restores, never lose data that the journal still holds.
- **Budget:** writing an entry stays under a stated time per edit, so it is never felt.

## 9. Build order
It is a thin layer if the core is built around **commands that change state** (every change goes through one function that also writes the journal entry). So: the journal writer comes with the core (the same step as the history and the take bank), recovery tools come later.

## 10. Open questions
The original journal UI/recovery questions are resolved by AMPLE Redux (chunk 101): the user-facing name is AMPLE Redux; retention is a configurable size budget with warnings and explicit, previewed compaction; browser drafts cover multiple files; and the history view supports read-only visual preview and return to the exact current editor state. Encryption remains off by default, with optional encryption documented in [Security](security.md).
