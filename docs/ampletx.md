# The .ampletx package

Status (2026-10-09): built. Export (Full and Derived), the read-only viewer, the comparison with this environment, the import (anyway, convert, new project) and the optional zip are in; format version 1.

An `.ampletx` is a **portable, single-file snapshot** of a saved song and everything it needs. It is a **text file**: binary files are Base64 lines, so the head, the manifest and the song can be read in any editor. Opening one is **read-only**; to work on it you import it, which makes an editable project and leaves the package untouched.

## Layout

```
#AMPLETX 1                  the format tag (a number to bump when the format changes)
#kind full                  full or derived
#manifest {...}             one JSON line (see below)
#entry {"path":..., "role":..., "encoding":"text|base64", "sha256":..., "size":..., "bodyBytes":...}
|a text line                text files: every line starts with |
QUJDRA==...                 binary files: Base64 lines of 76 characters
#end
...
#endpackage
```

Every entry names its SHA-256 and size; reading it back checks both. The same project gives the same bytes (entries are sorted by role and path; there are no timestamps except `created` in the manifest). `bodyBytes` lets a reader skip a body, so opening a package of hundreds of MB only reads the head.

## The manifest

`format` (`ampletx`, `amplet`, `spec`), `application` (version and the SHA-256 of the program that made it; nothing reads it back yet), `created`, `mode`, `project` (id, song, the SHA-256 of the saved song and of the whole state), `settings` (the song line, features, lint, plugin settings, the master seed and default engine of the project, render settings: sample rate, bit depth, resampler), `settingsFingerprint`, `dependencies` (every block with its template version, the renderer and toolprint of its take, plugins with version and fingerprint, models with their hash, every asset with hash and size), `master` (when included) and `warnings`. Interface preferences are not part of it.

## Full and Derived

**Full**: the song as it was saved, and every file its blocks read. By default only what the build really uses goes in (see below).

**Derived**: parts of the graph are rendered to audio and become new `import-audio` blocks (`derived/*.wav`); what is upstream of such a boundary is not in the package. Per audio path:

- **Automatic boundary**: the output of the first block after a source that really transforms the sound (an effect). A path that reaches a mixer without any transformation is rendered straight from the source and the export warns that it is untransformed.
- **Explicit boundary**: right click a block > *Bake to here for Derived export* (`# export bake <block>` in the song). It beats the automatic one and bakes as much as it covers.
- **Always include as-is**: right click a source > *Always include as-is in Derived exports* (`# export as-is <block>` or `<block>.<port>`): the source stays live and its files stay in. An as-is source and a boundary on the same path is a conflict that must be resolved.
- Paths are analysed per output; paths that share the exact same rendered result share one file. Disabled blocks and blocks that do not contribute to the final output are not exported by a Derived export (Full keeps them).

The export dialog shows all of this first: the files with hashes, the paths and boundaries, what is baked, what stays editable, whether the original source is embedded, the warnings.

**Comments are never removed.** A Derived song keeps every comment line of the original, including the comments above and inside blocks that are left out. A `# export …` marker or a `# view …` line that names a block that is no longer there stays as a plain comment (`# (Derived) export …`), so it cannot act on a block that is gone.

**Module instances.** A Derived export of a song with `use … as …` first writes every instance out as ordinary blocks (named like `intro/bass`) and cuts that text, so the package needs no module files and the baked parts can include blocks of a module. The export says so in its warnings. (Verified on a small song with one module: import and cold rebuild give the same SHA-256. A cut through a module with sample files was not tried.) A Full package keeps the `use` lines and the module files.

### FLAC for the audio inputs (optional, Derived only)

*Store the audio inputs as FLAC* makes the package smaller. FLAC is lossless in principle, but **there is no guarantee that the decoded audio equals the original file** (ffmpeg's encoder and decoder, the header of the WAV, the sample format). So the export does not just do it:

1. AMPLE sends every audio input through FLAC and back (ffmpeg, 16 or 24 bit integer PCM; other formats stay WAV).
2. It builds the final result twice in a temporary project: **before** (the original WAV inputs) and **after** (the inputs that came back), and tells you per input whether it came back byte for byte, only with a different header, or different.
3. You listen to both and press *I listened: use FLAC and write the package*; only then the package is written. The approval belongs to exactly that saved state and those options; a changed song needs a new listening test.
4. The package stores the `.flac` with the SHA-256 of the original WAV and of its samples. At import the audio is decoded again, compared with the original SHA-256, and the import result lists any input that differs. Import needs ffmpeg for a package with FLAC (it checks before it writes anything; `AMPLE_FFMPEG` points to it).

## Only what the build needs

The export runs one traced build and records what is used: which presets of a SoundFont and which samples of a sample set were really played. The package then carries a smaller, valid **SoundFont** with just those presets (zones, modulators, loops, sample headers and stereo links copied as they are, indexes renumbered; the package notes the original file, its SHA-256, the SHA-256 of the subset and the presets) and the samples that were played with all variants of their note or voice name. Imported projects render bit for bit the same. The option *Include the whole sets* puts everything in.

## Eligibility

Only a **saved** song can be exported: the export never saves for you. Unsaved changes in **any other song or module** of the project also stop the export (the message names them), because the package is made from saved files only. Every block that contributes to the final output must build; disconnected, disabled or unused blocks never block an export. The master preview (default on) is the render of exactly that saved state and is read straight from the package, without building.

## Import

1. Every file is matched by **SHA-256**: identical at the same place (nothing written), identical elsewhere in the project or in an installed pack (relinked), or extracted to a free place (the place can be changed first; nothing is overwritten, a taken place moves the file to `imported/<package>/...`).
2. The comparison with this environment lists differences (format, language version, block versions, renderers, plugins, sample rate, models) with their impact; nothing is normalised silently.
3. **Import anyway** uses this environment as it is and records the differences. **Try to convert** applies only the listed deterministic changes (relinks, moving a reference to an extracted file). **Create new project from package** makes a new project with a new identity; if differences remain a best-effort confirmation lists them. **Do not import** writes nothing.
4. Every import writes a record to `.ample/imports/` (source package, its SHA-256, the differences, what was written, relinked and changed).
5. A Derived package starts at its audio inputs; the blocks before them are not made up.

A package can also be compressed: `name.ampletx.zip` holds the same file; opening and importing handle it transparently.

## API

`GET /api/tx/export/preview`, `POST /api/tx/export`, `POST /api/tx/flac/preview`, `GET /api/tx/flac/audio`, `POST /api/tx/flac/approve`, `POST /api/tx/mark`, `GET /api/tx/inspect`, `GET /api/tx/master`, `POST /api/tx/import/plan`, `POST /api/tx/import/apply` (see `/api/openapi`).
