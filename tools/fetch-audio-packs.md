# Fetch audio packs

`fetch-audio-packs.mjs` resolves the selected pack definitions, clones each source repository partially, selects only the files described by its set rules, checks out those files and converts them to AMPLE WAV format with ffmpeg. It writes licence/source documents and set metadata beside the audio. By default the destination is the user's AMPLE library home; `--into` selects the repository's `audio-packs/` folder.

## Progress and metrics

The command prints pack and set file counts, phase start/end messages, elapsed milliseconds, conversion counters every 25 files and at set completion, and bounded retry counts. On a terminal command failure it reports the operation and exit status without printing raw subprocess output. A real fetch appends UTC timestamp, phase label, elapsed milliseconds and status to `fetch-audio-packs-metrics.tsv` in the output folder. It does not record filenames, source URLs, command output or audio content. A dry run prints progress but does not write files or metrics.

## Idempotency and options

The script can be rerun: selected WAVs and metadata are overwritten from their source definitions, and metrics are appended. Existing unrelated output files are left alone. `--dry-run` reports counts and estimated source size without writing; combine it with `--no-sizes` to avoid fetching blobs just to read their sizes. `AMPLE_PACK_CACHE` chooses the partial-clone cache; `AMPLE_PACK_OUT` overrides the destination; `AMPLE_PACKS_JSON` chooses a manifest; `AMPLE_PACK_BASE` changes the source base for local tests; and `AMPLE_FFMPEG` selects ffmpeg. Needs Node.js, git and ffmpeg.

## Measured timing

| Date | Run | Measured duration | Scope |
|---|---|---:|---|
| 2026-10-17 | `node --test tools/fetch-audio-packs.test.mjs` | 1.10 s total | Includes a dry run against a tiny local git fixture; no network fetch or production audio conversion was performed for this measurement. |

Real source fetch and conversion durations are recorded by the script on the destination machine; they are intentionally not estimated here.
