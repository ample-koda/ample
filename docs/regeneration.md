> Status (2026-10-02): built in chunk 58 (core service and CLI: `ample regen`, `verify-reproduction`, `explain`); the dialog and the health page entries come with chunks 76 and 77.

# Regeneration safety

The producer controls now offer **Check reproduction…** using the reference seed and current song/settings/software. The reference stays selected; configuration and provenance drift produce warnings. This is a comparison, not automatic replay of an older recipe. Recipe metadata can create a distinct take even when its audio bytes match. See [the recipe audit and review checklist](render-reproducibility.md).

**The rule in one line:** regenerating an AI block is only guaranteed to give the same result on the same machine and the same toolprint, so AMPLE treats every regeneration as a risky act: the old take is set aside first, the result is verified against it, and if it differs the default answer is **restore the previous take**.

## The warning (always visible)
On every AI block, on the regenerate button and in the freeze explainer:

> **Careful:** regeneration is only guaranteed to give the same result on this machine with the same plugin, model and GPU setup. On another machine, driver or version it may sound different. Your current take is kept safe either way.

The text lives in the spec (diagnostic/notice code `AMP5101`), so the UI, CLI and help show the same words. The block's toolprint (plugin, version, model hash, coarse hardware class such as GPU model and driver) is shown next to it, and the warning is stronger (`AMP5102`) when the current machine's toolprint differs from the one that made the take, or when the plugin's determinism is `unverified`.

## The flow when the user regenerates ("regen")
1. **Plan first (Always rule).** A new plan record is stored with `parentPlanId` = the plan of the current take, and a journal record `regen.started` is written first (write-ahead, chunk 45).
2. **Set the old version aside.** The current take is **not touched** (takes are immutable); it is pinned (protected from cleanup, included in the next backup, and a backup is suggested right now if the last good backup is older than the take) and marked `fallback` for this block. The block keeps playing the old take while the new one renders.
3. **Render** the new take (preview of progress as usual). Failure or cancel: the old take stays selected, a failed/cancelled plan remains, nothing else changes.
4. **Verify automatically.** Compare the new result with the old one:
   - identical content hash: **"Same result. Nothing changed."** The duplicate collapses (same content address), regen counts as a successful reproduction check and is recorded in the take's reproduction history.
   - not identical: measure and show how different (duration, loudness, spectral fingerprint, and for melody-plan blocks the note differences), with A/B listening.
5. **If different, ask what to do.** Options:
   - **Restore previous take** (default, pre-selected, also the result of closing or ignoring the dialog)
   - Keep the new take as a variant (old one stays selectable)
   - Replace with the new take (a dangerous act: typed acceptance, tombstone; the old take stays in history anyway)
6. **Crash safety.** The question is a *pending decision* stored in the journal and the history database, not just a dialog. If the app crashes, loses power or is closed while the question is open, the old take is still the active one (nothing was replaced), and on next start the health page and a dialog ask again. The default stays "restore previous". The new take is never promoted without an explicit answer.

## What counts as "regen"
Re-rendering a block whose seeds are unlocked or changed, the "regenerate" and "re-sing"/"re-write" buttons, and the explicit **Verify reproduction** command (re-render the same plan only to check, never replaces anything: it shows "same" or "different" and records the result). Changing a locked seed or a setting first triggers the seed warning (see [controllability](controllability.md)), then this flow.

## Reproduction history
Every take keeps a list of reproduction checks: when, on which toolprint, result (same / different / failed). That is how "verified on my machine" becomes a fact instead of a belief, and it feeds the determinism flag of the plugin (`unverified`, `verified-local`, `verified-cross-machine`).

## Where it is built
Chunk 55 (render path and fallback pinning), chunk 58 (flow, verification, reproduction history, pending decision), chunk 45 (journal records `regen.started/decided`, recovery of pending decisions), chunks 76/77 (dialog, warning text, A/B), chunk 10 (notice codes and wording in the spec).
