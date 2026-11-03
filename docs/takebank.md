> Status (2026-10-02): implemented in chunk 42 (Windows verified; Linux runs when CI first runs).

# Takebank and plans (as built)

The takebank stores everything AMPLE has ever rendered, **immutably**, together with the **plan** (the complete recipe) it was made from. It is what makes a song safe: the stored take is the guarantee, the seed is only a bonus (see [Determinism](determinism.md) and [Regeneration safety](regeneration.md)).

## The Always rule, enforced three times
1. **API:** `Takebank.BeginAsync(plan)` stores the plan first (a file and a database row) and returns a **ticket**; `ticket.CommitAsync(...)` is the only way to create a take, and a ticket works once.
2. **Database:** a take row needs a plan row. A trigger refuses a take without a stored plan even for someone who opens the database directly with foreign keys switched off (`AMP5220`). Plans, takes and their data references can never be updated or deleted (`AMP5224`); dropping a take is a **tombstone** flag, the record stays.
3. **Files:** the plan is also written as `.ample/takes/plans/<hash>.plan.json` and the take as `.ample/takes/<xx>/<hash>.take.json`, a plain copy that survives a damaged database.

## Plans
A plan holds: project id, block uuid and path, hash of the block's source, inputs (data or take ids with toolprints), seeds, the `rngVersion`, plugin id and version, toolprint, settings, intent (`preview`, `bake`, `variant`, `freeze`, `verify`) and the parent plan (for regenerations). Its **canonical JSON** is frozen (fixed property order, seeds sorted by name, other objects sorted by key, no white space, non-ASCII escaped, schema tag `ample-plan/1`) and the **plan id** is `sha256:` plus the hash of that text. The time is not part of it, so the same recipe has the same id. `spec-tests/plans/canonical-golden.json` pins the canonical text and ids; changing the form needs a new schema tag.

## Data and takes
Data blobs are content-addressed (`sha256:<hex>`, stored as `.ample/data/<xx>/<hex>`), streamed and hashed while written to a temporary file, then moved into place, so identical content is stored once even when several writers race. A take is its plan plus its data (role and id per blob); its id is the hash of those, so committing the same outputs for the same plan gives the same take. Every render **attempt** is recorded (`started`, `completed`, `failed`, `cancelled`, with the reason): a failed or cancelled render keeps its plan, and an attempt that stays `started` was interrupted.

## Reading, verifying, orphans, sizes
- `ReadTake` returns the take with its plan; blobs that are not on disk are listed as missing instead of failing, so the take can be re-rendered from its plan or restored from a backup (`AMP5221` when the data itself is opened).
- `VerifyAsync` rehashes the data and reports missing files, wrong sizes and bit rot.
- Blobs that no take references (an interrupted put) are found and **quarantined**, never deleted.
- Sizes are available per take and per block (distinct data counted once).

AI proposal builds may open the active project's takebank through `Takebank.OpenReadOnly`. Exact-plan audio is copied into the disposable proposal takebank; no plan, attempt, take, data row or blob is written to the source project. Existing project files that predate the setting use this cache by default; the AI setting can disable it.

## Commands
`ample takes list|show|verify [--project P]` and `ample plan show <take>`; a take id can be given as a unique prefix.
