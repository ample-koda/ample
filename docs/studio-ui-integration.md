# Extending the AMPLE Studio UI

The Advanced piano roll uses `roll-transport.mjs` for playhead/loop math and `roll-preview.js` for connected-instrument audition through existing client-side renderers. Preserve its explicit approximate/fallback label, exact effective MIDI-effect seeds, bounded cache and close-time disposal when adding views. Bar-crossing notes remain valid per-bar entries joined by `;tie=N`; never synthesize a GM program/channel merely to attach a tie.

Project exports use `project-export.js` and the token-protected `/api/project/export` job API. Long conversions/compression must remain asynchronous, show progress, support cancellation, and write only to the job's temporary directory. Do not buffer a completed archive in browser memory; download through the completed job endpoint.

Title-bar status controls use `.node-status`; completed render/cache states belong there, while `.tag.stat` is reserved for live build steps. Route error inspection through `showBlockStatusError` so the log panel, source diagnostic and upstream failure navigation remain consistent. New playback paths must use `playbackOutput(context)` after their processing/meters, or `registerPlaybackAudio(element)` for native audition audio. Do not put this tab-local 0–150% gain into offline rendering, exports, song parameters or recording capture. See [web-ui.md](web-ui.md#block-status-and-local-listening-volume-2026-10-10).

Canvas blocks use a two-row header: a compact cyan nameplate with an automatically sized title and pin toggle, then a toolbar with type, version, the current block's GUI/properties arrow, connected effects and the source-editor action at the right edge. Double-click the name to rename through the normal edit command; the title remains a drag handle, with pointer capture kept on the title so double-click or double-tap still reaches its rename handler. Two-finger touch gestures on the canvas zoom around their midpoint and pan the graph; native browser touch zoom is disabled only on that surface. RDP gestures forwarded as Ctrl+wheel use the existing canvas wheel zoom. Canvas geometry uses the complete 60-pixel header height for ports, links and layout. Volume blocks remain narrower and taller with their channel controls on the node. Each channel has a fader and a live output meter in dB; Link/Mono and channel mutes use compact switches. Live preview plays the resolved upstream signal through the block settings without rebuilding, and meter ownership follows that block rather than any unrelated audition. Built-take playback remains available separately.

Clicking the active bottom-panel tab collapses the panel; clicking a different tab or a tab while collapsed opens it. Tab clicks use the existing layout toggle so visibility controls and saved layout stay aligned.

This guide is for AMPLE plugins, built-in blocks, editors and panel work. It explains how to fit new work into the default dark Studio UI and how to tell whether existing work needs migration.

## Start here

1. Treat the Studio mockup as a historical reference only; it does not define current UI behavior and must not be updated. Read the Studio UI plan for current implementation guidance.
2. Read [the web UI status](web-ui.md) and the relevant chunk in the plan table. The plan distinguishes real behavior from mock-only behavior.
3. Search the existing UI before adding a new control. Reuse its components, token names, command path and panel registration.

## Visual language and controls

- Use semantic CSS variables from `web/styles/tokens.css` for surfaces, text, borders, focus and state. Use `skin.css` for common element skins and `studio.css` for advanced panels. Do not add a local hard-coded palette.
- Respect `data-theme` and reduced-motion preferences. Keep readable contrast and visible keyboard focus in every theme.
- Use `web/app/ui.js` helpers (`el`, `ask`, `showMenu`, `toast`) and existing field/control modules rather than building ad hoc dialogs or numeric editors.
- Keep controls keyboard operable and named. Sliders, knobs, curve handles, toggles and menus need keyboard behavior, an accessible name and appropriate ARIA state/value attributes.
- Use the shared `studio/knob.js` control for rotary parameters. Drag in normalized position and then apply the parameter's linear/logarithmic scale, matching its indicator; a linear value delta across a wide log range causes low values to hit minimum with tiny drags (verified in source, 2026-10-10). Keep incremental pointer deltas, an unsnapped drag position for stepped controls, and Shift fine control so changing sensitivity or reversing at an endpoint does not jump or stick.
- One pointer gesture should commit one edit. Preview the drag in the UI, then submit a single batched edit when it ends; Escape/cancel should leave the document unchanged.

### Source editor

The Source view is a locally bundled CodeMirror 6 editor. Its pinned dependencies and offline bundle are maintained by `package.json`, `package-lock.json`, `web/codemirror/ample-language.js` and `tools/build-codemirror.mjs`; rebuild with `npm run build:codemirror`. The AMPLE mode uses lexical facts from `spec/tokens.json` and the documented block-header shape. Theme colors use Studio tokens, and the bundle is lazy-loaded from `web/vendor/` with no CDN. Preserve the source view's LSP client, Apply/Save semantics, read-only file support, gutter, diagnostic/build overlays, links, completion, drops and keyboard actions when changing the editor. Focused browser acceptance is tracked in CM01.

## Adding a block, plugin or view

1. Define the block/template contract in `spec/templates/` and its language/data behavior in the applicable spec and tests.
2. Use the library and graph paths in `web/app/libraryui.js`, `canvas.js` and `graph.js`. Keep node state and each effect's bypass state separate. A node can use the independent `disabled` header flag and its context-menu toggle under Edit; it remains visible but produces no take, and required consumers are implicitly disabled with the warning AMP5253. Effects are ordinary typed AMPLE blocks joined by audio links. Open an effect's advanced controls to edit its own parameters; use the audio block's arrow to navigate to connected effects, with search when multiple are connected. Add → Effects → EQ & filters → Equalizer inserts a normal neutral EQ block connected in the graph. Keep new block actions in the existing Add, Build & export, Edit or Block tools context-menu groups instead of lengthening its top level. Graph links are the source of truth; do not add embedded chain managers or badge-based effect controls. Build, undo and source editing use the normal language/command path.
   A delayed context-menu action must read its group from the current store when clicked. Saving the view may reload group objects while a submenu is open, leaving references captured when the menu was built stale (verified 2026-10-16).
3. Preserve the existing port types. Inputs always sit on the left of a block; outputs always sit on the right. Audio and render ports display their compatible stream names on both sides: `audio` → `audio`, `render` → `render`. Tooltips retain the underlying port names, including numbered mixer channels; source connections retain those identifiers. The graph presents `midi` as Events. Event-processing blocks may be added later using existing event types and links, but do not imply that analogue signals, new event types, or new backend processing are implemented by the Studio UI.
4. Render ordinary parameter editing through the existing properties and `fieldFor` paths. Add bespoke advanced controls only when the block benefits from a visual editor; register it in `web/app/studio/panels/registry.js`. `web/app/studio/modal.js` owns the accessible dialog shell and transport. Panel modules belong under `web/app/studio/panels/`; share controls from `web/app/studio/`.
   Hidden properties tabs use `display: none`; only the active tab contributes height. Verified 2026-10-17: overlapping tabs hidden with `visibility: hidden` still reserved space, so a delayed master-level result in Info moved the Parameters action buttons down. Keep the actions immediately after the active content and cover delayed hidden-tab updates in layout tests.
5. Obtain current values through the document/store and send changes through `edit()` or the documented command API. Do not write project files directly from a component.
6. For custom visualizations, read tokens at render time. Canvas/SVG drawing must respond to the current theme, selection, enabled/bypassed state and reduced motion.

## Preview contract

- Label browser DSP or plot estimates as approximate. Do not present a visual estimate as a measurement of the integer renderer.
- The server effect preview is the exact audible reference for the same ControlService-selected input take, range and parameter values. It is bounded, does not create a take, and does not touch the takebank or journal. Keep the preview range and cache bounded. A preview request must use the take selected for the immediate upstream block; do not silently change selection as a preview side effect. An explicit Build and use input action may build and select that take.
- Offer the exact server preview only for types registered with the built-in effect preview route. Generic blocks can still expose input playback and ordinary properties, but must not send unsupported effect preview requests.
- Live local preview uses `web/app/studio/preview/audio.js` and the shared player: it loads the selected upstream take with the selected effect bypassed, then plays it through a persistent Web Audio approximation graph. Label the result approximate. While playback continues, knob movement updates the live graph immediately without restarting the audio; commit one document edit when the gesture ends. A/B toggles the selected effect's bypass in the active live graph, or replays the rendered comparison when rendered preview is active. Keep input playback and exact server-rendered preview under the `Compare / render details` disclosure.
- Generator blocks without audio inputs play their own built take. Keep their controls distinct from effect previews; do not require an audio input for generator playback.
- Keep unavailable preview controls disabled with a short reason (for example, no source take). Never build a take silently just to make a preview button work.
- For costly exact-render analysis, make the action explicit. The compressor's waveform and gain-reduction analysis only requests the bounded integer render after the user presses Analyze; show the analyzed range and distinguish input and output traces.
- Plot-based effect panels should update their curve, taps, room shape or timing readout from knob preview values, then commit through the shared control callback once at gesture end.
- Advanced preview transport exposes a Loop toggle beside Play for the full sample; Shift-drag selects a loop range. Keep the panel's loop preference across Stop/Play and transient preview reloads, and restore the pre-panel player loop state when closing. Native full-sample looping must survive `playTransient` replacing its audio source.
- Resolve effect preview input from the latest upstream build take, matching block playback; use separately selected takes only as the initial fallback. On input identity changes, cancel pending audition work and stop old preview playback. Key decoded audio by take identity. Verified source mismatch on 2026-10-10: the source block could play a new voice while Advanced still requested its older selected take. Server previews must ignore disabled audio/render links.
- A live spectrum uses the shared player analyser and unsubscribes when its panel is replaced or closed. A decoded take estimate is a separate display and must remain labeled approximate.
- Tempo-based effect playheads derive bar phase from the shared player clock and project tempo; dispose the player listener with the panel.
- Playback, transport and scrubbing should reuse the shared player rather than creating competing audio clocks.

## File fields and project assets

Use `web/app/fields.js` so each file field inherits its accept rules, picker, clear, open/reveal and drag/drop behavior. Internal drag payloads carry project-relative paths; OS drops go through the journaled upload/conflict flow, which reports per-file transfer progress. The shared picker windows lists above 2000 items; reuse `filelist.js` instead of rendering an unbounded list in a plugin panel. Do not bypass the command service for upload, overwrite, rename, move or delete.

## Do concurrent changes need migration?

Use this quick check when another AMPLE feature was started before a Studio UI change:

| Check | No migration needed when… | Migrate when… |
| --- | --- | --- |
| Layout | The feature already uses the current library/canvas/properties/bottom-panel shell and remains usable at narrow sizes. | It adds a competing full-page shell, hides required transport/panels, or breaks resize behavior. |
| Styling | It uses semantic tokens and shared skins, with focus and reduced motion handled. | It hard-codes colors/surfaces or loses contrast/focus under the current themes. |
| Editing | All project changes use the store/command path, remain undoable and preserve one-gesture/one-step behavior. | It writes files directly, bypasses journal/pre-image safety, or cannot be undone. |
| Effects/state | Effect bypass and node enablement remain independent; the source node's Advanced dialog manages its ordered connected chain, and each effect's parameter editor stays inside that same dialog. | A new path conflates node disable with effect bypass or lacks its canvas/property affordance. Chain edits must preserve type-safe links, terminal consumers and one-gesture/one-undo behavior. |
| Files | It reuses the project picker, accepted-file rules and project-relative drag payloads. | It adds a second picker/upload flow or writes around journaled file operations. |
| Help and proof | The web UI docs, help, parity matrix and a scenario/spec describe the behavior. | The behavior has no current documentation or the scenario no longer matches it. |

If all checks are in the “No migration needed” column, link the change to this guide and the full mockup in its handoff. Otherwise update the affected app region, mockup, docs, parity scenario and tests in the same implementation chunk. These migration rules apply only to AMPLE.

## Checks and references

- JavaScript syntax: `node --check <changed-file.js>`.
- Pure web unit tests: one focused `node --test web/lib/test/<file>.test.mjs`.
- Server/language change: build the touched project; run one focused `dotnet test --filter ...` when it fits the agreed narrow test window.
- Browser interaction: add/update a Playwright spec and its `.scenario.md`; run it only when browser/test-host execution is authorized for the task. AMPLE browser checks use a fresh Playwright Chromium context, not the Codex browser integration.
- Report synthetic-fixture Playwright results separately from active-demo results. Synthetic audio verifies UI and request wiring; it does not establish that a selected take in the active demo plays correctly.
- Always run `git diff --check`. A syntax/unit/build check is not visual or browser acceptance.
# Generation settings and reproduction (2026-10-17)

For audio cards, request bounded server waveform summaries; never fetch/decode every complete song just to paint a thumbnail. Serialize background summary requests, cache their in-flight promises, and release media sources on Stop. Every recurring poll needs an in-flight guard and teardown cancellation. Avoid redundant polling of a task already shown in its dialog. Close event streams in hidden tabs and reconnect on return. Browser regression coverage: `spec-tests/parity/ai-network-budget.spec.ts`.

Generation fields must display effective numeric defaults, including model-aware CFG, rather than leaving an ambiguous blank. Preserve explicit edits through source reload. Review the seed/CFG near miss checklist for source → request → take coverage. Producer controls expose Check reproduction with separate reference/new-render playback and warnings for changed configuration/software; the original take stays selected.
