> Status (2026-10-02): design sketch from a brainstorm. The facts marked MEASURED come from earlier ACE-Step experiments recorded in Prompt bridge; everything else is **expected behaviour** that has to be measured before the producer can rely on it. The clickable sketch shows the panel (properties of an AI block, "Controls").

# Controllability: what can the producer pin, tweak and expect?

AI blocks are not perfectly controllable and never will be: change the words or one of the seeds and you get something else. That is not a bug to hide, it is the material. The goal is that the **producer knows exactly what is pinned, what a tweak will do, and what it costs**, so they can work with it instead of being surprised.

## 1. What the producer is really after
Not "follow this exact melody", but: **get a melody I like, freeze it, then change around it** as far as the model allows, and be told honestly where the limits are.

## 1b. The Always rule: capture first, freeze later
The freeze ladder below is about what you **keep fixed on purpose**. Underneath it sits a rule that needs no choice: **every take stores its plan automatically, at the time it is made** (seeds, audio codes, settings, words, toolprint). The reason is the lost seeds of the songs "Always" and "Serendipity": a take you love and cannot get back. With automatic capture, "Freeze the melody" is just a label on something that was already saved, and a take without a plan can only come from an import (marked "no plan").

## 2. The freeze ladder (for a dynamic block such as the AI voice)
| Level | What is stored | What stays the same | What you can still change |
| --- | --- | --- | --- |
| **Free** | nothing | nothing: every build can differ | everything |
| **Seeds** | the two seeds (melody seed and performance seed) | the same take, **only if** words, settings, tool and computer are identical | nothing safely: any input change gives another take |
| **Melody (plan)** | the **melody codes** (the language model's output) plus both seeds and the settings | the melody and the structure | re-sing (new performance seed), restyle, voice, quality settings, tweak words at your own risk |
| **Take** | the rendered audio (baked) | everything, bit for bit | nothing until you unbake |
Why a "plan" level exists: in ACE-Step the language model first writes **audio codes** (melody, structure, timing) and a second stage (the diffusion synthesis) turns the codes plus text into sound. Storing the codes freezes the melody while the sound can still be varied. See [Bake](bake.md) for the take level and [Determinism](determinism.md) for reproducibility.

## 3. What happens if I change X (the table)
Columns: **melody**, **how the words fit** (do they land where they should), **sound** (timbre and performance). `=` unchanged, `≈` close, `≠` different, `?` unpredictable. Expected unless marked MEASURED.
| If I change... | Seeds frozen | Melody frozen (plan) | Take frozen |
| --- | --- | --- | --- |
| the words | `≠ ≠ ≠` (new input, new melody) | `= ? ≈` (words follow the stored melody only roughly: check timing, make variants) | `= = =` |
| style or mood tags | `≠ ≠ ≠` | `= = ≈` | `= = =` |
| tempo or key | `≠ ≠ ≠` | `? ? ?` (the codes carry their own timing: re-write instead) | `= = =` |
| the voice | `? = ≠` | `= = ≠` | `= = =` |
| steps or guidance | `= = ≈` | `= = ≈` | `= = =` |
| the melody seed | `≠ ? ≠` (a new melody: re-write) | ignored: `= = =` | `= = =` |
| the performance seed | `= ≈ ≠` (re-sing) | `= ≈ ≠` (the safe way to get alternatives) | `= = =` |
| model or tool version | `≠ ≠ ≠` (reproducibility lost, new toolprint recorded) | `= ≈ ≈` (stored melody re-rendered) | `= = =` |
| another computer | `≠ ≠ ≠` **MEASURED**: the same seeds gave another take | `= ≈ ≈` expected: re-rendered, not bit-exact | `= = =` |
Also **MEASURED**: a fixed melody seed gave a **byte-identical** take on the **same** computer; audio codes recovered from finished audio are **lossy** (only about a third of the digits matched), so a take cannot be turned back into its exact plan afterwards, which is why the plan must be stored at the time of the take.

## 4. What the producer sees (UI)
- In the properties of an AI block: **Frozen as** (Free, Seeds, Melody plan, Take) with a one-line meaning, the table above for the **current level**, a legend, and buttons: **Freeze the melody now**, **Bake the take**, **Re-sing...**, **Re-write...**.
- **Re-sing** opens the variants with the melody frozen (only the performance seed varies); **Re-write** varies the melody seed (new melodies). The variant cards say which of the two they vary.
- **Changing a parameter of a frozen block** is not blocked; a message says what to expect ("Changing steps while frozen as Melody: melody =, words fit =, sound ≈").
- In the text it is one word in the block header: `freeze seeds` or `freeze plan` (a baked take is `bake`). The linter treats a frozen block as fixed (rule L005 stays quiet).
- The cache key includes the freeze level, so changing it rebuilds the block and what depends on it.

## 5. What is stored in a plan
The plan is the "recipe plus the part you cannot recompute": the **audio codes**, the **melody seed**, the **performance seed**, all settings (steps, guidance, voice, tags, tempo, key, duration), the **words** and the **toolprint** (tool and model versions, platform class). With it, a re-render on the same toolprint reproduces the take; on a newer toolprint the melody stays and the sound is re-made. It lives in the take bank next to the take it came from.

## 6. Experiments that turn "expected" into "measured"
A small repeatable suite (run when a model or tool version changes, results stored with the toolprint):
1. **Same seeds, same machine** twice: identical? (already measured: yes.)
2. **Same plan, different performance seed:** does the melody stay (compare the codes and a melody extraction)? How different is the sound?
3. **Same plan, changed words:** how often do the words land within tolerance (word timing plugin)? How often is the melody still recognisable?
4. **Same plan, changed tags, voice, steps:** effect sizes on melody and timbre.
5. **Same plan, other computer or newer model:** how close (melody correlation, loudness, spectral distance)?
The numbers fill the table, and the symbols in the UI come from them (for example `≈` only when the measured melody correlation is above a stated threshold).

## 7. Relation to other parts
- **Variants:** the variant tool gains "re-sing" and "re-write" as two ways of varying; picking a variant can freeze it as a plan or a take. See [Variants](variants.md).
- **Bake:** the take level is bake; the plan level is a lighter bake that keeps the sound re-makeable. See [Bake](bake.md).
- **Language support:** `freeze` is part of the header syntax and the diagnostics. See [Language support](language-support.md).
- **History:** the freeze level and the plan are part of a revision; going back to a revision restores the same freeze state.

## 8. CLI twins
```
ample freeze <block> --melody|--seeds|--take      ample unfreeze <block>      ample variants <block> --re-sing|--re-write
ample plan show <block>                           ample plan export <block> > plan.json
```

## 9. Seeds: easy to change, locked by default
Seeds are the one control the producer will touch most, so it has to be both **easy** and **safe**.
- **Two seeds per AI block:** the **melody seed** (`lm_seed`) and the **performance seed** (`seed`). By default both are **derived** from the master seed and the block name, so the same song always asks for the same takes. The text can also hold a typed number (`lm_seed 3297468772`) or the word `derived`.
- **Locked by default.** The Seeds section shows the values with a lock; the plain parameter fields for them are disabled so a stray edit cannot change them. **Unlock to edit...** is one click and states the consequence for the current freeze level first:
  - *Free or Seeds:* "The current take can no longer be reproduced from the new seed. It stays in the take bank and in the seed history; bake it to keep it for sure."
  - *Melody (plan):* a new melody seed has no effect until the melody is unfrozen; a new performance seed re-sings the same melody.
  - *Take:* seeds are ignored until the take is unbaked.
- **Editing is easy once unlocked:** type a number, 🎲 for a random seed, **derived** to go back, **Lock the seeds** to lock again.
- **Nothing is lost:** every change pushes the previous seeds onto a **seed history** per block, with **use** (restore) buttons; after a build the panel compares the current seeds with the ones the last take was made with and shows a **warning** when they differ ("that take cannot be reproduced from these seeds").
- **Several tests for one block:** **Try several...** makes 4, 8 or 20 takes (variants) and the producer chooses. A variant can vary **both seeds**, **re-sing** (performance seed only, melody frozen) or **re-write** (melody seeds only). Each card shows its melody and performance seed. **Pick** puts the chosen seeds into the block and freezes it as *Seeds*; freezing the melody or baking the take makes it permanent.
- **In the text and the linter:** custom seeds are validated (the word `derived` or an unsigned 64-bit whole number from 0 to 18446744073709551615, error AMP2011) and raise the info-level lint rule **L009 custom-seed** ("fine and recorded; changing a seed later breaks reproduction of takes made with the old one"). A node with custom seeds shows a *custom seed* tag.
- **Why a warning and not a block:** a producer experimenting should never be stopped, only told. The only hard rule is that a baked take or a stored plan is never silently replaced.

## 10. Open questions
1. Is a **melody extraction** (notes from the take) shown next to the plan, so the producer can see and edit the melody and not only freeze codes?
2. Can **edited words** be re-aligned to a frozen melody automatically (syllable-aware), or is variant-and-check always needed?
3. Should the table also be shown **per model version** (different models, different rules), and how are results shared between users?
4. How is **the same plan on another computer** announced to the producer (a warning with the measured closeness)?

## As built (chunk 96)
The analysers and the melody plan are described in [Analysis](analysis.md): `ample analyze <take> --with melody` turns a take's pitch contour into the notes of a plan (`ample-melody/1`), which can be stored as the `melody` data of a take and pinned with `freeze plan`. Tolerances are from synthetic signals only; the numbers in the table above stay expectations until measured on real material.
