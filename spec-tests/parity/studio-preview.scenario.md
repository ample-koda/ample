# Effect preview parity scenario

1. Open an effect advanced controls before its upstream audio block has a take; Play input, Local preview and Hear the real thing are shown disabled, with Build input available when supported.
1a. Build input from that panel; verify preview, EQ input spectrum and compressor/distortion exact-analysis controls become available without closing and reopening.
2. Build the upstream input and reopen the panel; Play input, Local preview, Hear the real thing, A/B, the scrub slider and live output meter are available.
3. Start Local preview; verify the label says approximate, scrub to the middle, and change a control to hear the updated local render.
4. Request Hear the real thing, scrub to the middle and verify playback position changes.
5. Toggle A/B and verify the server request sends `enabled: off`; previewing does not create a take or change project history.
6. Verify requests longer than 30 seconds are refused and preview output is WAV.
7. In the compressor panel, drag or use arrow keys on the threshold and ratio handles; verify the transfer curve follows during the gesture and one edit commits when it ends. Analyze exact preview only on request and verify input/output waveform and gain reduction are labeled as exact renderer analysis.
8. Open EQ, filter, compressor, duck, distortion, delay, reverb and mixer advanced controls; verify each exposes keyboard-accessible controls and disabled-source preview actions remain explained.
8a. Open Mixer with a built source; verify the exact server effect preview stays disabled because mixer blocks have no effect-preview endpoint.
9. Move distortion drive, delay feedback/time, and reverb size/decay knobs; verify their plots/readouts update during the gesture and one source edit is committed on release.
10. On a distortion block with a built audio input, request Analyze exact preview and verify the bounded before/after waveform is labeled as integer-engine output; without an input take the action stays disabled.
11. Right-click a block and disable it; confirm it remains visible and faded, the block produces no take and can be re-enabled. In the server engine case, a required consumer is implicitly disabled with the warning AMP5253.
12. Open EQ during playback of the shared player; confirm the live spectrum trace moves independently of the dashed approximate spectrum from the built input, then close Advanced and confirm its player listener is released.
13. Open Duck while the shared player is active; verify its playhead moves through the 16 steps at the project tempo and its listener is released on close.
14. On Mixer Advanced, move each vertical gain fader and pan knob; verify the combined `gains` or `pan` parameter commits once per gesture and pan values remain left/centre/right.
15. On a standalone effect block, click its compact effect badge to open that effect's advanced controls; right-click the badge to bypass/re-enable it and verify its faded appearance and `aria-pressed` state update while the block remains enabled.
16. On an audio node, verify the Source and Advanced icons remain visible beside the title/class badge at the default zoom, even when its title or port labels need a wider card; use Fit and confirm links/ports remain aligned.
17. Double-click a block title to replace it inline, Enter to commit it as one journaled rename, Escape to cancel; entering an existing name should receive the next unique `-2`, `-3` suffix and every connection reference should follow the rename.
18. On a compressor with no audio source, move the Threshold handle with an arrow key; verify the displayed transfer curve follows immediately and the changed value commits through the block edit path.
19. On an audio source connected through EQ and compressor, verify its canvas badges appear in signal order, include faded state for a bypassed effect, open that effect's advanced controls on click, and toggle only that effect on right-click; the source node itself stays enabled.
20. With the browser set to reduced motion, open compressor Advanced, confirm the knob has no transition, and use the keyboard arrow to change its value.
21. On an audio-producing node, open Advanced and verify the ordered chain manager can add an effect, move an effect earlier/later, bypass it independently, and remove it. Confirm visible graph links and project source stay in sync, and undo restores each chain operation as one edit.
21a. Choose Edit on one chain effect; confirm its registered parameters and preview controls appear while the effect list remains visible in the same node Advanced dialog. Switch to another effect and back to block controls without closing or replacing the dialog.
22. Drag a compressor knob by pointer, confirm its readout updates during the gesture and exactly one undo entry is added; undo once and verify the original value returns.
