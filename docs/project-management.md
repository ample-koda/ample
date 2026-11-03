> Status (2026-10-11): partial. The CLI supports `ample new`, `ample open`, `ample doctor [--env]`, `ample instances` and project inspection; `ample open` attaches to or starts the server for a project. The lifecycle, template and management sections below include planned behavior and are not a claim that every listed UI, CLI or API operation is implemented. 

# Project management

Project management and history management ([History management](history-management.md)) are the two things the whole tool leans on, so this page goes into detail. It covers what a project is, where its data lives, how it is created, opened, copied, archived, moved and deleted, how settings are scoped, how health is checked and how format changes are migrated.

## 1. Vocabulary
| Term | Meaning |
| --- | --- |
| **Project** | A folder that contains a mandatory `.ample/` folder. Everything the project needs to reproduce its songs is inside it or reachable through its links. |
| **Home** | The per-user AMPLE folder (for example `%APPDATA%\ample` or `~/.config/ample`): global links, recent projects, plugin and model catalog, user templates, global settings. Never part of a project or a backup of a project. |
| **Engine** | The running `ample serve` process. One engine can serve several projects at once (each in its own browser tab). |
| **Link** | A named folder outside the project that is exposed in the folder browser (see Tech, "Linked folders"). Global or per project, read-only or read-write. |
| **Take bank** | The immutable, hash-addressed store of rendered audio and baked data of a project. |

## 2. What is in a project folder
```
my-song/                         the project (any name; it can be renamed or moved)
  song.amplet  lyrics/  midi/  chords/  packs/ ...     user content (sources)
  ample.lock                     pinned tools, packs, external data
  .ample/                        mandatory; AMPLE's own data
    project.json                 id (uuid), name, format version, created, master seed, modules, main line names
    history.db                   SQLite: revisions, lines, tags, checkpoints, operation log, tombstones
    takes/                       the take bank (hash-addressed, immutable)
    data/                        baked data blocks (hash-addressed)
    links.json                   project links (uuid, name, read-only flag, relative path hint); global links live in Home with their uuid
    assets.json                  uuids of project assets (assigned the first time AMPLE sees them; survive rename and move)
    vault/                       sealed credential store and its own timeline (no plain secrets)
    plugins.lock                 pinned plugin versions and toolprints
    cache/                       build cache (disposable, safe to delete)
    scratch/                     read-only checkouts and rebuilds (disposable)
    lock                         instance lock (pid, host, heartbeat)
    logs/                        engine logs (no prompts or lyrics: see privacy below)
  .ampleignore                   what the source scan and snapshots skip
```
Rules:
- **Disposable** means safe to delete at any time without losing information: `cache/`, `scratch/`, `logs/`.
- **Precious** means loss is permanent: `history.db`, `takes/`, `data/`, `vault/`, the sources and `ample.lock`. Backups cover these (profiles decide how much, see [Backup](backup.md)).
- `project.json` holds a **uuid**, not the path, so a project can be moved, renamed or opened from a backup restore and still be recognised in Recent and in the lineage records of releases.
- A folder with sources but without `.ample/` is not a project; the app offers "Make this an AMPLE project" (creates `.ample/`, first revision "adopted folder", scans the sources).

## 3. Lifecycle
### Create
A wizard (UI) or `ample new <folder>`:
1. **Name and location.** The folder name defaults to a slug of the name; the name itself is free text.
2. **Template** (see section 4).
3. **Modules** to enable (all off except core by default, see [Modules](modules.md)).
4. **Master seed:** random (default) or typed. It is shown once and stored in `project.json`; changing it later is a deliberate, logged act, because it changes every derived seed (and therefore every dynamic result not baked).
5. **Links:** pick global links to use here, add project links.
6. **First revision:** the project starts with the lines `studio` and `garage`, one revision "new project" on `studio`, you are on `garage` (see [History v1](history.md)).
7. The engine writes `.ample/`, takes the instance lock and opens the project.

### Open
- **Recent** list in the Projects manager (name, path, last opened, line and revision you were on, health badge, size).
- **Open folder:** a folder picker served by the engine (it only shows what the engine may see: the user's chosen roots and links). A folder with `.ample/` opens; a folder without offers adoption.
- **Drag and drop** a folder or a `.ample` export file onto the Projects manager; a `.ample` file is **never opened in place**: it is restored or imported into a new folder first (see section 7).
- **URL:** `http://127.0.0.1:5174/?project=<uuid>` opens a known project (tabs can be bookmarked).
- On open the engine takes the **instance lock**, checks the **format version**, runs a quick health check (a few seconds, never a full scrub) and restores your last view (line, revision, open tab). If you left unsaved work it is offered back (see [History management](history-management.md), "Unsaved work").

### Switch and close
Each browser tab is bound to one project. Closing the tab releases nothing by itself; the engine releases the instance lock when the last tab of a project closes (with a short grace period) or on shutdown. Unsaved work is kept as a draft, not lost.

### Rename, move, duplicate
- **Rename:** changes the name in `project.json` (and optionally the folder name). The uuid stays.
- **Move:** moving the folder is fine while it is closed; the Recent entry then says "moved? locate" and relinks by uuid when you point at it.
- **Duplicate** offers three explicit choices: **fork with history** (new uuid, history and takes copied or hard-linked, the lineage records "copy of"), **fresh start from the current state** (new uuid, no history, takes referenced by baked data only), **template from this** (strips takes and history, keeps structure).

### Archive and delete
- **Archive** marks a project as archived in Home (hidden from the default list, nothing changes on disk). It can offer to compress `takes/` and `cache/` later.
- **Delete project** is a dangerous act (see section 9): it shows what would be lost (revisions, takes in MB, releases that exist nowhere else), offers a backup first and asks for the project name to be typed. The default action is **move to the system trash**, not a hard delete.

## 4. Project templates
New-project templates are folders with an `.ample/template.json` and starter content:
| Template | Gives you |
| --- | --- |
| Empty | Core only, one tempo, no tracks. |
| Instrumental | Drums, bass, chords and a mixer block wired to a master chain. |
| Song with AI voice | The above plus lyrics, an AI voice block (plugin required) and a variants set. |
| Remix | Import audio or MIDI blocks, an overlap track, `based on` and `remix of` ready. |
Templates live in Home (yours), in a project (shared with its collaborators later) or in a pack. A template can pin plugin versions and links. "Save as template" strips history, takes, vault and secrets.

## 5. Settings and their scopes
| Scope | Where | Shared in exports/backups | Examples |
| --- | --- | --- | --- |
| **Global (user)** | Home | no | author name, default view (simple or advanced), global links, plugin catalog, default templates, theme, engine port |
| **Project** | `.ample/project.json` | yes | name, modules, assist level, tempo default, output folder, project links (without machine paths, see below), storage policy, backup profile and reminder interval, history view default |
| **Machine-local** | `.ample/local.json` (git-ignored, never exported) | no | absolute paths of links on this machine, plugin install paths, GPU/CPU choices |
Precedence: machine-local > project > global > built-in default. A project link records a **logical name** and, where portable, a project-relative path; the machine-local file maps names to real paths on this computer, so a restored project asks once "where is *Samples* on this machine?" instead of breaking.

Project settings that matter most:
- **Modules:** quality, credits, signing, git-mirror, secrets, lyrics (opt-in; turning one on is a normal revision).
- **Assist:** off, diff, button or auto.
- **Output folder:** a read-write link or the project folder; **a read-only link can never be chosen as output**.
- **Storage policy:** where big audio lives (inside, LFS, AMPLE store, external with accept-until), and the warnings that go with it.
- **Backups:** profile (`minimal`, `standard`, `complete`), reminder after N days or N revisions, last backup time shown in the Projects manager.
- **History:** default view (simple or advanced), cleanup is on request only (for now), automatic checkpoints are manual (for now).

## 6. Identity
Every revision records an **author** (from the global setting, default the OS user). It is a label for credits and history, not authentication: a local tool has no accounts. With the `signing` module the author also has a key (the private key is not in backups by default).

## 7. Import, export, backup and restore
- **Backup** (`ample backup`) writes one `.ample` file: a zip with `manifest.json` (project uuid, name, format version, profile, what is inside and what is deliberately left out, tool and plugin versions), the history, takes and data per profile, and optionally the sealed vault. See [Backup](backup.md).
- **Partial export** (`ample export`) takes a scope (project, line, tag, release or checkpoint) and a choice of contents (sources only, plus baked data, plus takes and audio, plus history, plus credentials sealed and off by default). It says plainly what is missing so the result can still be opened.
- **Restore** always goes into a **new folder** and never overwrites. It verifies hashes, reports what is missing (and why), asks once for the machine-local things (link locations, plugin availability) and then opens like any project. A restored project keeps its uuid unless a project with that uuid is already known, in which case you choose "restore as copy" (new uuid, lineage "restored from").
- **Import** handles: an `.ample` file (restore as above), a git repository (via the git mirror in reverse), an old recipe or `.ggufmusic`, audio and MIDI into blocks.
- **Round trip guarantee (goal):** backup, then restore on another machine, then `verify --hash` passes; `verify --repro` passes for everything recipe-exact on a declared platform class.

## 8. Health and maintenance
`ample doctor` (UI: **Health** panel with traffic lights and a "fix" button per item):
| Check | Depth | Fix offered |
| --- | --- | --- |
| Instance lock is ours / stale lock | quick | take over (typed, shows pid, host and last heartbeat) |
| `history.db` integrity (`PRAGMA integrity_check`) and hash chain | quick / full | restore the last good copy (a rolling `history.db.bak` per day) |
| Format version vs this AMPLE | quick | migrate (with automatic backup and checkpoint) or open read-only |
| Links present and reachable; read-only flag respected | quick | relocate, remove |
| Tools and plugins present with the pinned toolprints | quick | install, or accept the difference (reported in verify) |
| Take bank scrub (recompute hashes) | full (slow, on request) | re-fetch from backup, mark take as lost |
| Unreferenced takes and baked data (size) | quick | `cleanup` (dangerous act, see history management) |
| `cache/` and `scratch/` size | quick | clear |
| Free disk space vs take bank growth | quick | warn |
| Cloud-synced or network folder detected | quick | warn (SQLite and sync tools do not mix) |
| Last backup age | quick | back up now |
`ample doctor --fix` only does safe fixes (clear cache and scratch, rotate logs); anything that removes precious data goes through the dangerous-act pattern.

## 9. The dangerous-act pattern (shared by project and history)
Delete project, purge, cleanup, re-tag, unlock a frozen tag, change the master seed, take over a stale lock, restore over a known uuid. All of them:
1. Show a **dry run**: exactly what changes, how much space, which releases or lineage links lose data, what stays untouched.
2. **Suggest a backup** and offer to make it right there.
3. Ask for **typed acceptance** (the project name, the tag name or the scope), never a bare yes; in scripts `--accept "<what>" --reason "..."`.
4. Leave a **tombstone** in the operation log (who, when, what, why).
The UI uses one red dialog for all of them so the shape is always recognisable.

## 10. Concurrency, safety and privacy
- **One writer per project.** The instance lock (pid, host, heartbeat) stops two engines writing to the same `.ample/`. A second engine can open the project **read-only** (peek mode for the whole project) or take over a stale lock after the typed confirmation.
- **Crash safety:** SQLite in WAL mode; the take bank only ever adds files (write to temp, fsync, rename); a rolling daily copy of `history.db`.
- **Path safety:** the engine serves only paths inside the project and its links; `..` never escapes; read-only links are never written to, also not by plugins.
- **Privacy:** logs and the health report never contain lyrics, prompts or file names of takes; secrets live only in the sealed vault and never in `project.json`, exports (unless sealed on purpose) or logs.
- **Network:** the server binds to `127.0.0.1`; remote use is explicit, with a token and an SSH tunnel (see Tech).

## 11. Format versions and migration
`project.json` carries `format` (an integer). On open:
- **Same format:** open.
- **Older format:** the app offers to migrate. A migration takes an **automatic backup and checkpoint first**, runs in a transaction and is recorded in the operation log. A failed migration leaves the project as it was.
- **Newer format:** open **read-only** with a clear message (never try to write); unknown fields are always preserved on write by this version.

## 12. CLI twins of the UI
```
ample new <folder> [--template instrumental]     ample open <folder>        ample projects [--archived]
ample rename "<name>"      ample duplicate --with-history|--fresh|--as-template      ample archive|unarchive
ample delete-project       (dry run, backup suggestion, typed acceptance)
ample backup [--profile standard]      ample restore <file.ample> <new-folder>      ample export --scope tag:v1.0 ...
ample import <file|git-url|recipe>     ample doctor [--full] [--fix]      ample links [add|remove|list|set-readonly]
```

## 13. Open questions
1. Should Recent and project tags/colours live only in Home, or should a project be able to carry a colour for its tab?
2. Duplicate with history: copy the take bank or hard-link it (fast, but ties the two projects to the same files)?
3. Is a project allowed to be opened from inside a cloud-synced folder at all (warn, or refuse)?
4. How is the master seed change handled: a new project (fork) instead of an in-place change?
5. Do project templates travel in the `.ample` export when asked ("export as template")?
