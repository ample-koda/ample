> Updated 2026-10-17: the field-selection, deterministic proposal and plan-recording flow is implemented; focused verification has not been run yet. The sections below retain the broader workflow design.

# Variants: make many, pick one, keep a few

## Current implementation

In Studio, **Make variants** first offers the output-affecting fields supported by the selected block: numeric fields, fixed choices and seeds. Select one or more, then review the exact deterministic values for each take before rendering. Explicit seed parameters such as `seed` and `lm_seed` accept the full unsigned 64-bit range and are stored exactly in the take plan. The synthetic **Renderer seed** field varies the renderer's normal seed; explicit seed parameters are separate choices. Free text, file paths and execution-policy fields are not proposed.

Variant values are temporary build overrides while rendering, and each take's immutable render plan records the changed field names and exact before/after values. **Apply & pick** writes every listed block parameter (including numeric controls and full-width seeds) into the working `.amplet` source, then selects the rendered take. The synthetic renderer seed stays attached to the take because it is not a block parameter. The selected values can therefore be inspected, saved and built again from Properties or Source. Repeating a request can reuse existing takes. If no field is selected, no render starts.

Takes render sequentially. Each completed take appears in the review grid before the next starts. **Cancel current take** aborts the active render; completed takes remain in the take bank and visible in the grid. Each request carries the original song version and variant index, so editing the song stops the remaining batch and stopping/retrying preserves the same proposed values.

This support is intentionally based on each template's declared parameter metadata. A field is offered only when its metadata says it affects output and its kind is currently supported. Tests cover proposal repeatability and that chosen values are recorded in the take plan; actual sound quality remains for the producer to judge.

Generating several takes of a dynamic block (for example an ACE-Step vocal) and choosing is the everyday workflow, so it needs to be fast. A **variant set** is a batch of takes of one block, a **pick** locks one, and a **shortlist** keeps a few alternatives for later.

## Make
```
$ ample variants chorus --count 20            # 20 takes of the block "chorus"
$ ample variants chorus --count 20 --vary seed,voice=[anna,bo]   # also vary other things
```
- Seeds come from AMPLE's stable PRNG (master seed plus block name plus variant number), so "variant 7 of chorus" is the same take next time on the same declared platform; nothing is lost when a run is repeated.
- Only the block and what depends on it is rendered. A budget (`--max-minutes`, `--max-gpu-hours`) and cancel apply, because 20 renders cost GPU time.
- Every take lands in the **take bank** (immutable, hashed, with its full recipe) and in a numbered variant set, `chorus/v-2026-10-02-1`.

## Review (UI and CLI)
- A grid of the variants with waveform, length and any measured scores (timing, quality module); play, play A/B against the current pick or another variant, loop, solo against the rest of the song ("in context").
- **A score of 0 to 100 per variant, set by the user**, plus stars/flags (reject, maybe, good), a short note and tags. These human fields are kept apart from measured scores, which are labelled with the scorer that made them (so a human 80 and a measured 63 never get mixed up).
- Quick keys: arrows to move, space to play, 0 to 9 to score (0-90), r to reject, k to shortlist, Enter to pick.
- Sort and filter by score, flag, measured timing error, similarity to the pick.

## Pick and lock
```
$ ample pick chorus 7                         # use variant 7 and lock it (freeze to its take hash)
$ ample pick chorus 7 --keep 3,12,15          # lock 7, keep 3, 12 and 15 as alternatives for later
$ ample pick chorus --best                    # take the highest user score
```
- **Pick = lock:** the block now refers to that take by hash, so later renders and releases are stable.
- **Shortlist (`--keep`)** stays attached to the block as **options**: they remain in the take bank and are listed with the block (`ample options chorus`), so you can switch (`ample pick chorus option 12`) or open the review again with only the shortlist. Switching is a normal revision, so it can be undone.
- **Everything not picked and not kept** stays in the take bank as unreferenced takes until a deliberate `cleanup` (see [History v1](history.md): purge is dangerous, with impact and backup suggestion). Unpicked takes of a sealed release are never touched.
- Picking, scoring and keeping are recorded in history (who, when), so "why did we choose 7" can be answered later.

## In the file
```
voice chorus_vox  using anna  { lyrics chorus }
  variants { count 20  vary seed }      # recipe: how the set is made
  pick 7                                # the locked choice (take hash recorded in ample.lock)
  keep 3, 12, 15                        # shortlist
  score 7 = 92, 3 = 85, 12 = 80         # the user's own 0-100 scores (optional)
```

## Decided details (2026-10-02)
- **UI first.** This workflow is a **per-block tool in the web interface** (right-click a block: "Make variants...", review grid, pick, keep). Most users will not use the CLI, so the CLI examples above are the scriptable twin of the same actions, not the main way in.
- **Round two (`refine`)** is a good idea but **later**: variants close to a chosen take (same voice and prompt, small change, with a small/medium/large distance setting), offered per block in the UI, with a CLI twin afterwards.
- **Automatic scorers to start with (all four, as plugins on a variant set):** lyric timing error, key/pitch fit, loudness and dynamics, similarity to the chosen take (finds duplicates or deliberately different ones).
- **Several blocks together (`--together verse,chorus`):** per block separately by default; with `--together`, whole-song variants are made (variant 7 = verse 7 + chorus 7) and judged as a whole. In the UI: a "vary together" toggle when several blocks are selected.
- **Scores:** a free number from 0 to 100, optional, with stars and flags as shortcuts that translate to a number; everything stays optional and apart from measured scores.
- **Scorers are opt-in.** Each automatic scorer is a plugin that may need its own model; a project enables only what it needs, and a scorer that is not installed shows as "not available" in the review grid instead of a blank score.
