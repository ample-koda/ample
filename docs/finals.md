# Finals: several deliverables of one song

> Status (2026-10-11): built (studio, API). A `ample finals` command is not built yet.

A song can deliver several **finals**: the full version, an edit, a teaser. Everyday building does not make them; **Render** does, on purpose, and says first how big and how long it will be (the estimate of [Storage](storage.md)).

## What a final is
`name`, the **first bar**, **how many bars** (0 keeps everything to the end), a **fade in** (beats), a **fade out** (bars) and the **format** (`mp3` or `wav`). A song without any has one implicit final, `full` (the whole song). Finals are kept per song in `.ample/finals.json`.

The API accepts names of 1–60 ASCII letters, digits, spaces, `-` and `_`; `fromBar` is clamped to 1–100000, `bars` to 0–100000, and each fade to 0–64. `wav` selects WAV; other or omitted format values use the MP3 default. Listing returns each definition with its last rendered take id, path, time and byte size when available.

## Rendering one
Projects, Finals (or `POST /api/finals/render`): the estimate and the disk check first (AMP5604 when it does not fit), then the build of the song, then the kept bars of the master with their fades, exported to `renders/<song>-<final>.<format>`. The master take is recorded with the final, so a clean-up never removes it. An unknown final is AMP5605.

## API
`GET /api/finals?song=`, `POST /api/finals` (add or change), `POST /api/finals/delete`, `POST /api/finals/render`.
