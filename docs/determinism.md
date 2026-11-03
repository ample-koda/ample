> Status (2026-10-02): design sketch from a brainstorm. Nothing here is built or tested.

# Determinism in AMPLE

## Default: strict
`require reproducible` is on. A build **fails** if a node is not reproducible and has not been recorded.

## Four levels (declared by every plugin)
| Level | Meaning | Examples |
|---|---|---|
| exact | same input = same bytes on the declared platform | rendering notes, mixing, alignment math |
| machine-exact | same on the same machine/build/driver; different elsewhere | AI synth on a given GPU |
| seeded-drift | a seed controls it, but it differs per machine | the language-model part of the AI |
| nondeterministic | differs every time | unseeded or cloud models |

## Three verbs for anything that is not exact
| Word | Meaning | Reproducible? |
|---|---|---|
| `lock` / `freeze` | record the result and reuse it | yes, from the recording |
| `pin vN` | choose an earlier recording | yes |
| `refresh "reason"` / `allow nondeterministic "reason"` | every time again, the difference is accepted | no: explicit consent, shown in the report |

## Retries and checks (see examples/05-lyrics)
- A check has a severity: `warn` only reports, `error` fails the node.
- A retry policy must be explicit and bounded: `retry up to N takes until checks pass`. Each attempt gets a fixed, derivable seed
  (`name + attempt number`), so the sequence of attempts is reproducible; the chosen take is frozen.
- On failure the policy says what happens (`keep best`, `fail build`), never an endless loop.

## How it stays true
- `ample verify` runs a plugin's self-test twice and compares hashes; a plugin that lies about its level is downgraded.
- A platform fingerprint (os, gpu, driver, plugin build) is stored with machine-exact results; on another machine AMPLE uses the frozen audio and warns before any re-render that would differ.
- Seeds per node are derived from the node's name, not its position: adding a section shifts nothing.
- Internally always lossless (WAV/FLAC, 24-bit); mp3 only on export.
- Honest limit: bit-exact floating-point results across different CPUs cannot be guaranteed in general; "exact" means "same hash on the declared platform class".
- **What is guaranteed (three layers):** (1) a stored take plus the deterministic mix is bit-identical, verified by hash; (2) everything AMPLE renders itself (built-in blocks, mixer, PRNG) is identical on every supported platform, proven by golden hashes; (3) regenerating an AI block is **only guaranteed on the same machine and toolprint**. For (3) the app shows a permanent warning and runs the safe regeneration flow (old take set aside, result verified, default answer "restore previous"): see [Regeneration safety](regeneration.md).
# Generation recipe audit (2026-10-17)

Seeds alone are insufficient. The CFG investigation exposed missing effective defaults and discarded plugin provenance; see [the incident and mandatory end-to-end checklist](render-reproducibility.md). Preserve the full recipe and immutable reference audio. A comparison with changed models/software may proceed with warnings; byte identity must be measured rather than inferred from a seed.
