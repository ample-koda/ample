# Storage: the takebank, protection and clean-up

> Status (2026-10-11): overview, protection rules, clean-up (UI, CLI, API), soft limit, options, size and time estimate, compressed takes and the data path on another drive are built. Finals are also built; see [Finals](finals.md). Remaining storage plan parts: [plan/storage](../plan/storage/README.md).

Every render is kept as a take in `.ample/data`, so a song never renders twice. Over time that adds up (a dubstep session of a day reached 22 GB). The Storage page (Projects, Storage) and `ample storage` show where the space goes and give it back safely.

## What is never removed
One function decides (`StorageService.Classify`); nothing else does. A take is **protected** when it is:
- **chosen** (the selected take of a block), a **variant** or part of a regeneration decision;
- **baked or pinned**: its id is named in a song or module of the project, or in any history revision (so tags, checkpoints and releases keep their takes);
- the **current take of a block** (the newest), and the takes it was **made from**;
- a **final**;
- **younger than the keep window** (default one hour), so a running build or an open preview is never touched.

Everything else is reclaimable: **superseded** takes (an older take of a block that has a newer one, beyond the "older takes to keep" option) and takes whose **data is already missing**. A removed take is tombstoned (its record and plan stay) and its data is deleted (or moved to the archive folder); when the song needs it again it simply renders again.

Never touched: the `renders/` folder and every other file of yours.

## Dry run, impact, progress
- **Studio:** Projects, Storage: a bar (protected / can be given back), the reasons, the free space of the drive; "See what a clean-up would do…" shows the impact (takes and GB per reason and per block, free space before and after, what stays protected); "Clean up" runs it in the background with a **progress bar** and **Cancel** (it stops between items and leaves a consistent store).
- **CLI:** `ample storage show`, `ample storage clean` (dry run, prints a fingerprint), `ample storage clean --accept <fingerprint>`, `ample storage settings --soft-limit-gb 10 --keep-superseded 1 --archive-path D:\ample-archive`.
- **API:** `GET /api/storage`, `POST /api/storage/plan`, `POST /api/storage/clean` (the fingerprint of the dry run must still match), `GET /api/storage/status`, `POST /api/storage/cancel`, `POST /api/storage/settings`; progress also goes to the event stream (`storage.progress`, `storage.done`).
- Temporary working files (finished AI tasks, `.ample/tmp`) older than a day are part of the dry run.

## Soft limit
`softLimitGb` (default 10; 0 is off) never deletes anything: when the project is over it, or the drive has less than 5 GB free, the studio shows a hint with the real numbers and the CLI says so.

## Options (`.ample/storage.json`, also in the Storage page)
| Option | Default | Meaning |
| --- | --- | --- |
| `softLimitGb` | 10 | size above which a clean-up is suggested |
| `keepSuperseded` | 0 | older takes of each block that stay besides the current one |
| `keepMinutes` | 60 | takes younger than this are never cleaned up |
| `archivePath` | none | a clean-up moves the data here instead of deleting it |
| `dataPath` | none | the takebank on another drive |
| `intermediates` | compressed | `compressed` or `wav`: how new WAV takes are stored |
| `silenceRuns` | true | configured in the file; the current renderer does not read this option (F2) |

## Smaller takes
- New WAV takes (64 KB and up) are stored compressed: a lossless container (16 bit byte planes + Brotli). The decoded bytes are exactly the bytes that were rendered, so the content address, plan ids and take ids do not change, and silence and quiet passages shrink to almost nothing; a measured sample of a real project went from 303 MB to 89 MB (29 percent).
- Takes made before this stay plain until you run **Projects, Storage, Compress the existing takes** (or `ample storage compress`): lossless, each file is decoded and rehashed before it replaces the plain one.
- The option `intermediates` (`compressed` default, or `wav`) turns it off for new data. The same data is stored once whatever it is called (content addressing); the overview counts take rows that share data.

## The takebank on another drive
`ample storage move D:\ample-takes\song` (or the Storage page) moves the take data there with progress and sets the `dataPath` option; `ample storage move -` moves it back. New data goes to the new place at once, reads look in both places while it moves, and a stopped move continues when run again. When the folder is not reachable (an unplugged drive) a write says so (AMP5603) and what is missing renders again.

## Estimate and disk check before a build
- `GET /api/build/estimate?path=` (and the first line of every full build in the build log) says how many blocks have to render, about how many GB they will store and how long it takes. At first this is an **educated guess** from the length of the song (a stereo WAV per block; light blocks fast, GM, plugin and AI blocks slow); after each finished build the per-type size and render pace are updated with an exponentially weighted average (alpha 0.3) in `.ample/analysis/build-stats.json`, so the next estimate says "from N earlier builds". The stats are per project, not per machine.
- A build of more than 2 GB asks first. A build that does not fit on the drive of the takebank (the estimate plus 20 percent) is refused before anything is written (AMP5604) with what a clean-up could give back; small builds skip the check.
- The build log reports the progress with an estimate of the time to go.

## Errors
A full disk gives AMP5600 ("not enough free space on the drive of the takebank") and an unwritable store AMP5601, with the block named, instead of a bare I/O error.

## Clean-up safety rules (audit 2026-10-20)

- Anything the clean-up cannot read (a source file, a history revision, a file in `.ample`, `storage.json`) stops the plan with AMP5606 and nothing is removed; it never guesses "nothing there".
- Protected as well: take ids written in any file of any history revision (also of dropped lines), in finals, in assistant proposals and sessions under `.ample`, in sources of any size, and every id written anywhere in a plan (inputs).
- The archive folder must be separate from the take data folder (AMP5603). `ample storage move` only moves take data files (64-digit hex names in two-digit folders), refuses a destination inside or around the old folder, verifies each copy byte for byte before deleting the source, and never deletes a file that is its own copy.
- Temporary files: only direct children of `.ample/tmp` and `.ample/ai/work|validate|audio`, untouched for a day (newest file inside counts), never through a link, and never the folder of an AI task that is not yet applied, rejected or cancelled.
- Not protected on purpose: the AMPLE Redux journal (`.ample/blackbox`) is not read; its sources come back through the history revisions. A build running at the same moment is covered by the one-hour window, not by a lock.
