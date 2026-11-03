> Status (2026-10-02): implemented in chunk 40 (Windows verified; Linux runs when CI first runs).

# Project layout and safety (as built)

A project is a **folder with a mandatory `.ample/` folder**. Everything the user writes (songs, lyrics, chords) lives next to it; everything AMPLE manages lives inside `.ample/`. The design background is in [Project management](project-management.md); this page describes what the code does.

```
<project>/
  *.amplet, lyrics/, chords/ ...     the user's sources
  .ample/
    project.json                     id, name, created, format version, master seed, settings
    history.db                       history database (chunk 50)
    takes/  data/                    the takebank (chunk 42)
    blackbox/                        AMPLE Redux journal (legacy storage path; chunk 45)
    locks/instance.lock              held by the one writer (operating-system lock)
    locks/instance.json              heartbeat: process, machine, started, last seen
    tmp/                             scratch, safe to delete while the project is closed
    backups/                         copies of project.json made before a format upgrade
```

## project.json
Canonical JSON (fixed property order, indented, LF). The master seed is stored as a decimal string. A new project's master seed is derived from its id unless given. The id never changes when the folder is moved or renamed. Settings: AMPLE Redux (budget 10 GiB, 90 days full detail, a year thinned), cleanup (no automatic age limit), mounts (filled by the library, chunk 85), default engine.

## Format versions and upgrades
The current format is **1**. Opening a project for writing upgrades an older format: the old `project.json` is first copied to `.ample/backups/project.v<N>.<time>.json`, then the upgrade steps run and the result replaces the file atomically. Opening read-only never upgrades (`AMP5209`). A project made by a newer AMPLE is refused (`AMP5206`). Fault tests prove that an error at any step leaves either the old or the new file complete, and the backup intact.

## One writer
The writer holds an operating-system lock on `.ample/locks/instance.lock` and writes the heartbeat. A second writer is refused with `AMP5204` that names the holder; read-only opening always works. **A held lock is never broken automatically**; taking over is a dangerous act that comes with the history work (chunks 50 and 77). `ample doctor` reports whether the lock is held or only an old heartbeat remains.

## Atomic writes
Project files are written to a temporary file (`*.ample-tmp`) in the same folder, flushed to disk, renamed over the target, and the folder is flushed where the platform allows it. A crash leaves the old file, the new file, or a harmless leftover that the doctor reports (`AMP5213`).

## Safe paths
All project paths go through `ProjectPath`: relative only, no `..` escape (`AMP5201`), no invalid characters, trailing dot or space or Windows reserved name (`AMP5202`), and no symbolic link or junction that leads outside the project or a registered link (`AMP5203`).

## Control: seeds, freeze, variants, regeneration (as built, chunk 58)
**Seeds** are stored per block and locked by default (derived from the master seed); changing one needs an unlock first (`AMP5260`: it probably makes reproduction impossible), and every change is kept in the seed history. The **freeze ladder** is read from the block header: nothing (free), `freeze seeds`, `freeze plan` (the melody output of the selected take is passed back to the renderer as a pinned input and becomes part of the plan), or `bake` with a selected take (that exact take is used and nothing is rendered; if its data is missing the build says so with `AMP5221` and does not render). **Variants**: `ample variants` makes several takes of one block, each with its own seed derived from the master seed; asking again gives the same ones; you choose one, keep others and discard the rest (a tombstone). **Safe regeneration** (`ample regen`, see [Regeneration safety](regeneration.md)): the selected take is never touched; the new result is verified against it; identical means "same result"; different becomes an open decision, stored in the database, whose default is to restore (keep) the previous take, so a crash or a closed dialog leaves the old take in place. Replacing the take is a dangerous act. `ample verify-reproduction` re-renders the take's own plan only to check, and the result goes into the take's reproduction history. `ample explain` says what each kind of edit does at the block's freeze level.

## Build (as built, chunk 55)
`ample build <song>` orders the blocks by their connections and, for each block, builds a plan from its source (type, name, class, parts, parameters and connections; **not** the bake, lock or refresh flags), the takes of its inputs, its derived seeds, the renderer and the settings. If a take for that plan exists it is used (a cache hit); otherwise the plan is stored first (the Always rule), the block is rendered and the take committed. Changing one block therefore re-renders it and what depends on it, and nothing else. `--preview 5:4` renders four bars from bar 5 at a low sample rate (a separate plan from the full build); `ample bake <song> <block>` renders at full quality and adds `bake` to the block header; `ample play` writes a preview WAV. The built-in renderers (midi-clip, drums, bass, piano, mixer, master, group) use integer-only synthesis (fixed-point oscillators, tables computed with decimal arithmetic), so the same song gives the same bytes on every machine. Blocks without a renderer (AI voices and other plugin blocks) are skipped with `AMP5251` until plugins exist.

## History (as built, chunk 50)
`ample history|checkpoint|tag|fork|go|peek|promote|drop|cleanup`. The snapshot of a state is the set of source files (`.amplet`, `.amplelrc`, `.lrc`, `.chordpro`, `.lyr`, `.chords`), each stored once in the takebank; a node is immutable and stored in `history.db`. A new project has the lines **studio** (the real version) and **garage** (work happens here). **Checkpoint** snapshots only when something changed. **Go** first checkpoints unsaved work automatically (kind `auto`), so switching never loses anything, then restores the target's files through the command bus. **Peek** reads another state without changing anything. **Promote** shows the changes first (`--yes` does it) and replaces studio's head with garage's; the old state stays in the history. **Tags are locked**; moving or removing one, **dropping** a line (archive, recoverable) and **cleanup** are dangerous acts with dry run, typed acceptance, reason and tombstones. Cleanup only removes file contents that no active line and no tag can reach, and never content used by a take.

## Command path and AMPLE Redux (as built, chunk 45)
Every state change is an `ICommand` run by the `CommandBus`: the pre-image (what is about to be overwritten) is captured, a **start** record is written to the journal and made durable, the change is applied, then an **ok** or **failed** record follows. If the journal cannot be written the command is not run (`AMP5230`). AMPLE Redux currently uses the compatible `.ample/blackbox/` storage path and `*.seg` files (JSON lines, each with a CRC32C and the SHA-256 of the previous record, so a changed record is detected). A torn last line (a crash while writing) is dropped on open; a damaged record in the middle is **never repaired away**: the file stays as evidence and new records go to a new segment. A command with a start but no outcome record is **interrupted** (`ample recover`); the legacy `ample blackbox restore <seq>` command restores a file from a pre-image as a new command. A copy of the journal tail is kept in the user's home (`%LOCALAPPDATA%/Ample/blackbox-mirror/<project id>/`). Retention: full detail for 90 days, then hourly summaries for a year, within a 10 GiB budget (the legacy `ample blackbox compact` command; the newest segment is never thinned). The journal is not encrypted (optional encryption is chunk 92). Dangerous acts share one contract: dry run, backup suggestion, typed acceptance, reason, tombstone record (`AMP5232`).

## Commands
`ample new <folder> [--name N] [--seed N]` creates a project. `ample open <folder>` attaches to or starts its local server; `ample project open <folder>` opens it for writing, prints a summary and releases the lock. `ample project info <folder>` prints the summary read-only. `ample doctor <folder>` checks structure, project file, lock state, leftovers, unknown entries and free disk space against the AMPLE Redux budget, and changes nothing.

## Library (chunk 85)
The catalog is per user (`library.db` in the home folder), not per project. A project keeps `ample.lock` in its root (assets, packs and plugins pinned by hash), an `assets/` folder for imported files (each with a `.provenance.json`), and the ids of its project links in `project.json` settings (`mounts`). A writable linked folder carries `.ample-catalog.json`. See [the library manager](library-manager.md).
