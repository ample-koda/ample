> Status: the **reusable block modules** (`use`, module files), the **quality checks** and the **credits** are built (see "As built" at the end). The opt-in feature switches, the plugin catalog and the settings screen below are still the earlier design sketch.

# Opt-in modules and plugins

The AMPLE core stays small: text sources, a timeline, stable random, history, take bank, hashing and rendering through plugins. Everything else is a **module** that a project switches on, or a **plugin** (an external tool behind one contract). Nothing is installed or enabled by default except the core.

## Enabling
In the project (`.ample/` settings or at the top of an `.amplet` file):

```
features {
  quality    on        # scoring, levels, rule packs
  credits    on        # contributors, weights, declared vs measured
  signing    on        # signed releases and tags (layers 5 and 6 of tag protection)
  git-mirror off
  secrets    on        # backup secrets sealed inside the backup
  lyrics     on        # timed lyrics, checks, queries
  assist     diff      # off | diff | button | auto
}
```
A module that is off adds no commands, no files and no checks. Turning one on is a normal revision. A project that needs a module the installed AMPLE does not have says so on opening ("needs: quality >= 1.2"), and still opens read-only.

## Modules (part of AMPLE, opt-in)
| Module | What it adds | Notes |
| --- | --- | --- |
| `quality` | Scorers, levels (`draft`, `review`, `release`), rule packs, experiment mode | Levels start weak and tighten. Experiment mode relaxes rules, marks results as tainted and expires. A human rating stays the gate for a release. |
| `credits` | Contributors, roles, per-block weights, declared versus measured splits | Records, not legal conclusions. |
| `signing` | Signed releases and tags, `verify` of who set what | The private key is not in backups by default. When on, tag layers 5 and 6 are on by default. |
| `git-mirror` | One-way mirror to git, full conversions both ways | Raw git objects are kept so the round trip is lossless. |
| `secrets` | Credential store with its own timeline | Sealed with a master passphrase, recovery key offered; see [Backup](backup.md). |
| `lyrics` | Timed words, checks, queries, own `.amplelrc` format and importers | Checks that need word timing need the alignment plugin. |
| `assist` | Proposals for text, timing and arrangement | A project setting for how much is automated: `diff` (shown as a patch), `button` (one click makes a revision), `auto` (applied on its own line, reviewed later). |

## Plugins (separate programs, opt-in per project)
Plugins talk to AMPLE through one contract; AMPLE never links against a model.

- **Transports:** in-process (a .NET library), out-of-process (JSON-RPC over stdio), http, and an MCP adapter (untrusted by default).
- **Kinds:** renderers (ACE-Step voice, instrument engines), analyzers/scorers (word timing, pitch/key fit, loudness, similarity), separators (voice from mix), fingerprinters (strict, content, loose hashing), importers/exporters (lyric formats, MIDI), panels (JS UI, sandboxed iframe).
- **Manifest** (`plugin.json`): id, version, transports, what it offers, **toolprint** rules (what identifies the build), compatibility ranges, models it needs and where they come from, and the permissions it asks for.
- **Long jobs** are async with progress and cancel. Artifacts travel by hash, never by path.
- **Models:** often separate downloads (word timing, pitch, separation). A plugin declares them; AMPLE shows "not installed" and offers the install, and never silently falls back. A scorer that is missing shows as "not available" in the review grid.
- **Trust:** plugins are pinned by hash in `ample.lock`; an updated plugin has to pass `upgrade-check` (does the release still reproduce) before it replaces a pinned one.

## Versions and compatibility
Every module and plugin has a version, and a project records the range it works with. Compatibility groups are proven by canary renders, not just claimed (see [Determinism](determinism.md) and Tech). Format versions apply to `.amplet`, `.amplelrc`, `.ample` exports and `plugin.json`; readers keep unknown fields.

## Open details
- Which modules ship in the first version (suggested: core, `lyrics`, `quality` minimal, `secrets`).
- Where the plugin and model catalog lives (per project, with an optional shared user-wide one, as for soundsets).
- How permissions are shown and approved (network, disk, GPU).

## Plugin settings screen
Every plugin has two entry points in the sidebar (and in the project settings), next to its enable checkbox, name and version:
- **ⓘ Properties** opens a small dialog: what it does, id, version, status, toolprint, permissions, the models it needs (with missing ones marked), which blocks use it now, and a summary of its settings. From there: *Show in the library* or *Advanced settings...*.
- **⚙ Advanced settings** (only for an installed plugin) opens a **full-screen editor** that takes over the editor area, like the lyrics editor:
  - **Tabs** per plugin, declared in its manifest (for the voice renderer: General, Models, Pipeline, Performance, Permissions). Fields are typed (select, number, toggle, text) so the editor needs no plugin-specific code.
  - **Ordered lists with drag and drop** for things that have an order: model priority (the first enabled model that fits is used), pipeline stages (required stages cannot be switched off), file types to index, stems to produce. Rows can also be moved with the arrow buttons, and every list has **Sort A → Z**, **Enabled first** and **Required first**.
  - **Undo and Redo, one step at a time** (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z outside text fields), as long as there is something to undo; the buttons show how many steps are left. **Reset to defaults** is a step that can be undone too.
  - **Two ways back:** **Apply and back** (keeps the settings in the working copy) and **Undo and back** (throws the session's changes away, with a confirmation if anything changed).
- **Plugin settings are project settings** (stored with the project, pinned alongside the plugin version in `plugins.lock`), so they travel with backups and exports and show as **unsaved** until Save. Machine-specific values (device, threads, install paths) belong to the machine-local settings, see [Project management](project-management.md).
- **Permissions** are part of the same screen (network access, reading the project and linked folders, writing only to the output folder). A plugin cannot get more than its manifest declares.

## As built

### Reusable modules
A module is a file `modules/<name>.module` (project, linked folder or pack) and a song uses it with `use <module>[@version] as <name> { ... }`. The syntax, expansion rules, seed scoping and version pins are in [language spec 07](language/07-references-and-modules.md). In short: the tool expands every `use` into plain blocks named `<instance>/<block>` before building, so each instance has its own stable seeds; `ample.lock` pins module name, version and hash (AMP5328 when one changed); the editor keeps showing the short `use`.

- `ample modules list` shows the modules of the project; `ample modules add <file>` checks a file and copies it into `modules/`; `ample modules expand <song>` prints the expansion and the problems (AMP5320 to AMP5327).
- `ample check`, `build`, the variants and regeneration commands and the server's build routes all build from the expanded song.
- Not done: the node editor does not draw a module instance as a node (the source editor shows it), a module cannot move its blocks on the timeline, and there is no sharing or catalog of modules (out of scope).

### Quality checks
`ample check quality [--take id]` measures the newest take of every block that has audio (or one take) and applies the rules of `spec/quality-rules.json`:

| Rule | What | Default limit |
| --- | --- | --- |
| Q001 clipping | runs of three or more full-scale samples | none allowed |
| Q002 true peak | 4x oversampled peak | -1 dBTP |
| Q003 loudness | integrated loudness (ITU-R BS.1770-4, gated) of the master | -14 LUFS within 1.5 LU |
| Q004 silence | longest quiet stretch inside the song (lead-in and tail excluded) | 2 s |
| Q005 DC offset | mean value | 0.01 |
| Q006 sample rate | a final take not at 44100 Hz (previews are not checked) | - |
| Q007 tempo and key fit | needs an analysis plugin; reported as "not run" until then | - |

Results are kept per take in `.ample/quality/<take>.json` (derived data, easy to make again). They are advisory. `.ample/quality.json` can set the target and tolerance, thresholds per rule, switch rules off and name the rules that **must** pass before a tag or a promote (`{"require":{"tag":["Q001","Q002"],"promote":["Q001"]}}`); then the newest take of the master block is measured at that moment and a failure stops the tag or promote with AMP5333.

Limits: only 16-bit PCM WAV (mono or stereo, mixed down) is measured; the true peak uses a cubic interpolation, not the 48-tap filter of the standard; there is no UI for the results yet (the data is in the files and the command output).

### Credits
`ample credits [--format md|json] [--out file] [--write]` makes the credit sheet from the plans of the newest takes (plugin, version, voice, toolprint), the modules and the library assets that the songs use (with licence and credit as kept in the library), and the people in `.ample/credits.json` (`{"usage":"commercial","people":[{"name":"...","role":"words","note":""}]}`). Licence warnings (AMP5332): a non-commercial asset when the project has a release (a tag, or usage `commercial`), a no-derivatives asset, an asset without a licence. **Advisory, not legal advice.** `--write` keeps the sheet in `.ample/credits/`; `ample export` and the server's export refresh it first, so every export carries a sheet made from what was really used.

### Analysis
Tempo, key, loudness, spectrum, pitch and melody analysers and the `analysis` plugin contract are in [Analysis](analysis.md). They are plugins by design (never in the core) and are not wired into the quality rules yet.
