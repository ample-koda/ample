# Fetch the General MIDI SoundFont

This helper installs the MuseScore General SF2 bank and its license/provenance files into `%LOCALAPPDATA%\ample\audio-packs\licensed\musescore-general\` by default (or `AMPLE_HOME\audio-packs` when `AMPLE_HOME` is set). The 206 MB bank is ignored by git; only provenance and the downloader are tracked.

## Steps

1. `--download` streams the three named files from the OSU Open Source Lab mirror of MuseScore General.
2. It enforces a 512 MiB SF2 limit and 16 MiB text-file limit, checks the SF2 RIFF/SFBK header and SHA-256, then atomically renames each downloaded file from `.part`.
3. Once installed, `--verify` checks the bank against the pinned SHA-256 and confirms the accompanying license and source listing are present.
4. Each download and verification step appends its UTC start time, result and elapsed milliseconds to `audio-packs/licensed/musescore-general/fetch-gm-soundfont-metrics.tsv`.

## Idempotency

An already present SF2 is accepted only when its SHA-256 equals the pinned digest. A mismatching file is left untouched and the command fails. Partial downloads are deleted after failure. Provenance files already present are not overwritten. Metrics are append-only and contain step names, status and elapsed time, not asset contents.

## Usage and environment

Run from the AMPLE checkout:

```text
node tools/fetch-gm-soundfont.mjs --download
node tools/fetch-gm-soundfont.mjs --verify
node tools/fetch-gm-soundfont.mjs --download --into
```

Requires Node.js with built-in `fetch`, `Readable.fromWeb`, and HTTPS access to `ftp.osuosl.org`. `--into` retains the repository `audio-packs` layout; `AMPLE_PACK_OUT` selects a specific pack folder for automation.

## Timing and size

2026-10-04, this machine: SF2 size 215,614,036 bytes; download completed in approximately 90 seconds. License and sample-source metadata were fetched in the same run. Future measured per-step durations are appended to the target metrics TSV.
