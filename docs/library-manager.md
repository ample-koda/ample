> Status: the service, the CLI and the API are built (see "As built" at the end); the library screens are not. The rest of this page is the design; where the built version differs it says so below, and section 8 (the screen) describes the planned screen only.

# Library manager

A song depends on things that are not in its text: sample packs, sound fonts, voices, plugins and their models, block templates, external data, imported audio, lyrics and chord files. The **library manager** lists all of them in one place, shows **where each one is used**, and lets you **replace** one by another (with an impact analysis), also **per folder**. It builds on the **linked folders** (mounts) from Tech: global catalogs reach AMPLE as read-only links, and the library manager is how you browse and use them.

## 1. What is in the library
| Kind | Examples | Comes from |
| --- | --- | --- |
| drum kit, synth pack, sound font, sample, audio import | `kit/tape`, `felt-piano`, `old-take.wav` | a **catalog** (a linked folder, global or project) or the project folder |
| text file | `lyrics/chorus.lyr`, `chords/main.chords` | the project |
| voice | `anna`, `bo` | a plugin (the voice engine) |
| plugin, model | `acestep 0.9`, `acestep-turbo-q8` | installed, with their pins in `ample.lock` |
| block template | `drums 1.2`, `piano pinned 2.0` | the template library |
| external data | `weather.example/api` (locked 2026-10-01) | recorded once, part of the project |
Every entry has a **name**, **kind**, **version**, **hash** (content fingerprint, strict, content or loose per Tech), **size**, **where it lives** (catalog or project folder, with the virtual path), **access** (read-only or read-write) and a **status**:
- **ok:** present and matches the hash in `ample.lock`.
- **changed:** present but different from the pinned hash. You can accept the new hash (a change to the working copy) or restore the pinned file from the catalog or a backup.
- **missing:** not reachable (a link that points nowhere, a file that was deleted, a plugin that is not installed). **Relocate** repairs a link without touching the project (a machine-local setting).

## 2. Identity: every catalog and every asset has a uuid
- **Each catalog gets a uuid** the first time it is linked (`cat-7f3a-91c2`). The uuid, not the path, is what projects, `ample.lock` and the usage index refer to, so a catalog can be **moved, renamed, put on another drive or opened on another computer** and everything keeps resolving. Only the machine-local mapping "this uuid lives at this path here" changes.
- **How a folder is recognised as that catalog:** the uuid is written to a small marker file in the catalog root (`.ample-catalog.json`) when the link is writable at link time; for **read-only media** (a locked drive, a shared network folder) AMPLE cannot write one, so it keeps the uuid in its registry in Home together with a **fingerprint of the catalog** (a hash over the sorted file hashes, sampled). Relocating to a new path runs an **identity check**: marker uuid match, or fingerprint match (with a percentage when files were added or removed). A mismatch says "this looks like a different catalog" and asks before relinking.
- **Project assets get a uuid the first time AMPLE sees them** (`ast-3c1d-9b02`), stored in `.ample/` and in the usage index. A **rename or move** (a refactor) keeps the uuid; a copy gets a new one and records "copy of". Catalog assets get their uuid from the catalog index (a hash-derived id plus the catalog uuid), so the same file in two catalogs is two assets.
- **In the song and the lock:** the song text shows **names** (readable); `ample.lock` stores **ids and hashes** (`asset ast-3c1d-9b02 "kit/tape" @ cat-7f3a-91c2 sha256:...`). Renaming an asset or moving a folder changes the name shown, not the id, so history, tags and sealed releases keep pointing at the same thing. Two catalogs with an asset of the same name are told apart by catalog uuid.
- **Duplicates and conflicts:** two links that report the same uuid are the same catalog (a mirror); AMPLE uses the one that is reachable and warns when their fingerprints differ.

## 3. Global asset catalogs: linked folders ("hard links")
- A **global catalog** is a folder (for example `D:\Music\Samples`) linked into AMPLE for every project; a **project link** exists for one project only. Links are **AMPLE-level virtual links**, not filesystem hard links (see Tech, "Linked folders (mounts)").
- A link is **read-only or read-write**. **A read-only link is never written to (by AMPLE or by plugins) and can never be an output folder**. Catalogs are almost always read-only.
- AMPLE **indexes and fingerprints** a catalog (content hashes, pack hashes, fingerprinter plugins; incrementally and cached), so the song can refer to an asset by **id (shown as its name) and hash**, and a changed or moved file is noticed.
- The library manager shows catalogs with a link and a lock icon, their folders as a tree, a **missing** badge when the link is unreachable, and offers: **Relocate** (where is this on this computer), **make read-only / read-write**, **Unlink** (dangerous act: it lists how many uses would turn missing), **Link a folder**.

## 4. Reference or copy (and what a backup contains)
Using a catalog asset **references** it by id (shown by name) and hash; nothing is copied. That keeps the project small but means the asset must exist where the project is opened. Two ways to make the project travel:
- **Copy into project** (per asset or folder): the files go into the project (`packs/`), the song can then point at the copy (use Replace), and backups and exports contain it.
- **Backup profiles** decide what happens with referenced assets: *by reference* (only name, hash and size are recorded; restore says exactly which catalog assets are needed), or *include the assets that are in use* (vendoring): AMPLE copies exactly the used files into the backup. See [Backup](backup.md).
A restored project asks once where each named catalog lives on this machine (the machine-local setting), then verifies the hashes.

## 5. Where is it used? (the usage index)
- **Now:** the working copy is scanned: block parameters that name the asset (a kit, a set, a voice, a file, a data source), block types for templates, needed plugins and models. Uses inside groups and templates are found too and show the full path (`chorus_vox › ACE-Step render`).
- **In history:** a **reverse index** in `history.db` (`asset_use(asset hash, revision, block, parameter)`), updated when a revision is made, answers "in which revisions, on which lines, in which tagged or sealed releases is this used". Old revisions keep their reference; nothing in history changes when you replace something now.
- **Navigation:** each use has **Block** (shows it in the node editor) and **Source** (jumps to the line); the properties of a block show a library button next to a parameter that names an asset.
- **Unused:** assets nothing uses now. Project assets that are unused are cleanup candidates (see below); catalog assets are never removed by AMPLE.

## 6. Replace, with the impact analysis
Replace changes **references in the working copy**, never the catalogs, and never history.
1. Pick the asset, tick which uses to replace (all by default), choose a replacement of the **same family** (a drum kit by a drum kit, a sound font by a sound font, a voice by a voice, a lyrics file by a lyrics file).
2. The **dry run** shows: each use and its change (`drums.kit: kit/tape → kit/808`), the blocks that will rebuild (their cache keys change, so they and everything downstream turn grey), **sealed or tagged revisions that still use the old asset** (they keep it: a release stays reproducible), whether the replacement comes from a read-only catalog (referenced, never modified), and how to undo.
3. A **thick warning** (the shared dangerous-act dialog): it changes what the song sounds like; **typed acceptance** (the asset's name), a **backup suggestion** and an optional reason (kept in the operation log).
4. Undo: revert in the source editor, go back to the last revision, or replace it back. Replacing is not saved until you save.
Not replaceable by reference: block templates (updated per block with *Update*), plugins (installed, updated or removed in the plugin list) and models (they belong to a plugin).
**Replace a folder:** choose the folder or catalog that takes over; assets are matched by relative name and family; the dry run shows the **mapping** (pairs, and the unmatched assets that keep their old reference); then the same warning and typed acceptance. Useful when a library is reorganised or a better version of a kit pack arrives.

## 7. Other operations
| Operation | Applies to | Notes |
| --- | --- | --- |
| **Relocate** | a missing link | identity check by catalog uuid (marker file or fingerprint); machine-local; the project, ids and hashes stay the same |
| **Accept the change** | a changed file | records the new hash in `ample.lock` (working copy) |
| **Rename, move** (refactor) | project assets and project folders | the asset uuid stays; the names in the song are updated automatically (not a replace: the content is the same); not offered for read-only catalogs |
| **Copy into project** | catalog assets | makes the project self-contained for that asset |
| **Remove** | unused project assets | dangerous act: dry run, backup suggestion, typed acceptance; files go to the system trash; older revisions may no longer rebuild unless the take bank or a backup has them |
| **Clean up unused (project)** | all unused project assets | same dialog, listing everything |
| **Verify all** | everything | re-hashes and compares with `ample.lock` |
| **Link a folder / Unlink** | catalogs | Unlink lists how many uses would turn missing |

## 8. The screen (planned, not built)
```
┌ Library ─────────────┬ search [____]  filter [all ▾]  [Clean up unused] [Verify all] ┬ asset: felt-piano ──────────┐
│ All assets (31)      │ name              kind        version where        size status used │ sound font · 2.0 · 140 MB     │
│ CATALOGS (linked)    │ kit/tape          drum kit    1.4     🔗🔒 Samples  64MB  ●      1 use │ where: global catalog Samples │
│ 🔗🔒 Samples (5)     │ felt-piano        sound font  2.0     🔗🔒 SoundF.  140MB ● changed 1  │ status: changed (pinned 09-12) │
│   📁 kit (3)         │ grand-piano-v3    sound font  3.0     🔗🔒 SoundF.  620MB ●      unused│ Used now (1)                  │
│   📁 loops (2)       │ ...                                                                    │  ☑ piano · set  [Block][Source]│
│ 🔗🔒 SoundFonts (3)  │                                                                        │ In history: 8 revisions; v1.0  │
│ ✎ Project folder (3) │                                                                        │ [Replace...] [Accept change]   │
│ ✎ Stems (missing)    │                                                                        │ [Copy into project]            │
│ INSTALLED            │                                                                        │                               │
│ Plugins, Models, Block templates, Voices, External data                                       │                               │
└──────────────────────┴────────────────────────────────────────────────────────────────────────┴──────────────────────────┘
```
Selecting a **folder** in the tree shows a summary (how many assets, how many in use, link path, access) and folder actions: *Replace this folder with...*, *Relocate...*, and *Rename / move* (project folders only).

## 9. Safety and integrity
- The engine only serves paths inside the project and its links; `..` never escapes; read-only links are never written, also not by plugins.
- A catalog can change under the song (someone edits a file): the hash comparison reports **changed**, `verify` fails with a clear message, and builds that depend on it say so. The song never silently uses a different file than the one it was made with.
- Replace and Remove are dangerous acts (shared dialog, see [Project management](project-management.md)); Relocate, Accept the change, Rename and Copy are not.

## 10. CLI twins
```
ample lib list [--kind kit] [--unused] [--missing] [--changed]       ample lib where <asset>        ample lib verify [--lock]
ample lib replace <asset> <replacement> [--only block1,block2] [--dry-run]       ample lib replace-folder <folder> <folder>
ample lib relocate <link> <path>      ample lib copy <asset> [--into packs/]     ample lib rename <asset> <new>     ample lib remove <asset> --accept "<name>" --reason "..."
ample links add|remove|list|set-readonly       ample lib index <catalog> [--rebuild]
```

## 11. Open questions
1. Should **block templates** also be replaceable in bulk (every `piano 1.0` by `piano 2.0`) with the same impact analysis, or stay per block?
2. How are **same-named assets in two catalogs** shown and resolved (ambiguity, priority order of catalogs)?
3. Should a catalog be able to carry a **manifest** (names, versions, licences, tags) that AMPLE reads, so a catalog author can describe their assets?
4. **Licences and credits:** should the library record each asset's licence and feed the credits module (with weights) automatically?
5. How much of the **reverse index** is rebuilt on restore versus stored in backups?
6. Should the catalog marker file also carry a manifest (name, description, licence, version), so the uuid and the catalog's description travel together?
7. What happens when a catalog is **forked** (copied to a new place and then edited): a new uuid with "copy of", or an explicit "this is the same catalog" choice?

## As built
**Where:** `Ample.Core/Library` (`Library`, `LockFile`, `UnlinkMountAct`), the CLI `ample library list|scan|link|add|relocate|verify` and `ample lock update|verify`, and the API routes `/api/library*` and `/api/lock*` (see [the API](api.md)).

| Thing | How it works |
| --- | --- |
| Catalog | One SQLite database per user (`library.db` in the home folder: `AMPLE_HOME`, else the local application data). Mounts and assets; project links carry the project id. |
| Mounts | Linked by a person naming a folder; nothing is found by itself. Read-only unless you say `--read-write`. A writable link gets a marker file `.ample-catalog.json` with the catalog uuid; a read-only one is never written to, so its identity is a fingerprint of the first thousand file names and sizes. State is worked out live: online, offline (folder gone) or other (a different catalog is at that path). |
| Assets | Uuid (`ast-...`) on first sight, kept across renames, moves and content changes (a changed file is a new revision, same uuid). Name = the path without extension (`kit/tape`), or `name@version` for packs and plugins. Kinds come from file names; only files of known kinds are opened, for hashing (sha256). Other files are listed by name and size and never read. Hidden folders and files are skipped. |
| Scan | Incremental: size and modification time unchanged means not read. A new file whose content was just lost from another path is the same asset moved. Gone files are marked missing, never deleted. Duplicates are found by hash. |
| Relocate | Identity check: the marker has the same uuid, or the fingerprint is the same, or at least 80 percent of the known assets are there with the same size; else AMP5292 unless forced. No song and no lock file changes. `FindMoved` looks for the marker two folders deep under given roots. |
| Read-only | Every write goes through `ResolveWritable`, which refuses read-only mounts (AMP5293), offline ones (AMP5290) and paths that leave the mount. |
| Resolution order | project folder (`<name>` or `assets/<name>`, with or without extension), the project's linked folders, the user's linked folders, installed packs. Missing: AMP5291 (AMP5290 when its folder is offline); pinned hash differs: AMP5294. |
| `ample.lock` | Written in a fixed order (assets, packs, plugins; by name) so the same project gives the same bytes: `<id> "<name>" mount <catalog uuid> <hash> size <n>`. `update` works out what the songs use (a parameter value that is an asset name, or starts with it and a `/`); `verify` says ok, changed, missing or offline per entry. |
| Packs | `pack.json` needs a name (letters, digits, `-`, `_`, `.`) and a version. Installed by copy into `<home>/packs/<name>@<version>` or linked where it is. The AI assistant can trigger this itself from a public `.zip` URL through `ample_install_pack` (`GET /api/ai/context/install-pack`): downloads it, extracts it, finds `pack.json` at the root or inside one wrapping folder, and calls the same copy-install path. Always needs the user's approval (an `AiApproval` of kind `installPack`), even when the link was in the request, since it changes the shared library rather than the task's disposable workspace; at most 200 MB. |
| Import | Copies an asset into `assets/` of the project with a `.provenance.json` next to it (source asset, catalog, hash, time), as a command recorded in AMPLE Redux. Never over an existing file. |
| Unlink | A dangerous act (dry run, typed words `unlink <name>`, reason): the folder and its assets leave the catalog, no file is touched. |
| Usage index | For the open song: block, parameter and line of every use (the "now" half of section 5). |

**Measured** (2026-10-03, Windows 11, Debug build, one machine): scanning 50,000 small files took 6.5 s the first time (all hashed), 1.8 s when nothing changed (none read) and 1.7 s with one changed file (one read); making the files took 26 s. With 5,000 files (the default of the test): 0.8 s, 0.2 s, 0.2 s. `AMPLE_PERF=1 dotnet test ample/tests/Ample.Core.Tests --filter Category=Library` repeats the 50,000 run.

**Not built yet:** the screens, file watching (a rescan is explicit), the reverse index in `history.db` (the "in history" half of section 5), replace with impact analysis and replace folder, accepting the change of one asset (a lock update accepts all), fingerprinter plugins, voices and models as catalog entries (they belong to plugins), rename and move refactors of project assets, the language's own diagnostic for a missing asset (no block parameter is typed as an asset yet, so a missing asset shows up when the build resolves it), and network mounts beyond what the identity check gives.
