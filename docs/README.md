# AMPLE (Assisted Music Programming Language Experience)

**Status (2026-10-11): this is AMPLE's design and knowledge base, not a statement that every described idea is implemented. Each feature page carries its own built/planned status; use the [build plan](../plan/README.md) for chunk status.** The rationale below is historical. Mock examples and proposed CLI sessions are labelled on their pages.

**Origin:** two favourite songs, "Always" and "Serendipity", could not be reproduced because their seeds were never stored; AMPLE is built so that this cannot happen again (the "Always rule" in [Decisions](decisions.md)).

The original goal was a clean, standalone tool (C#) for writing songs as text: instrument tracks (textual MIDI) and AI voices/samples on one timeline, reproducible by default, with a web UI (plain HTML/JS/CSS on a local server, also on Windows). It does not depend on the prompt bridge or prompt-music; those (and the ACE-Step findings in Prompt bridge) are only background knowledge.

**Building it:** the build plan is in [`../plan/`](../plan/README.md) (chunks prefixed with their priority, lowest first). The **language specification** is in [`language/`](language/README.md) (normative, kept maintained); the generated `help.html` comes from chunk 12.

## Pages
| Page | What it covers |
| --- | --- |
| [Decisions](decisions.md) | What was decided so far, grouped by topic, and what the design is based on. Start here. |
| [Mono and stereo](stereo.md) | The `channels` parameter (auto, mono, stereo), the song default on the master block and the stereo meter. |
| [Piano roll](piano-roll.md) | Piano-roll tools, gestures, note properties, MIDI expression, accessibility and remaining engine gaps (PR01-PR22). |
| [Project layout (as built)](project-layout.md) | What a project folder contains and how it is protected: `.ample/`, format versions and upgrades, the one-writer lock, atomic writes, safe paths, and the `new`/`open`/`doctor` commands. |
| [Takebank and plans (as built)](takebank.md) | Immutable takes and the plans they were made from: the Always rule enforced in the API, the database and the files, canonical plan ids, content-addressed data, attempts, verification, orphans. |
| [Storage: overview, protection and clean-up](storage.md) | What the takebank uses, which takes are never removed, the clean-up with dry run, impact and progress, the soft limit and the per-project options. |
| [Finals](finals.md) | Several deliverables of one song (full, edit, teaser), each rendered on purpose with an estimate first and exported with its own cut and fades. |
| [Score import](score-import.md) | Turning a MIDI or MusicXML score into a sequence (parts, patterns, playlist, tempo) with ample_import_score. |
| [Access outside the project](ai-access.md) | How the assistant asks to write outside the project (what, why, how long), the pass-through commands, the emergency stop and the project log. |
| [The language server (as built)](lsp.md) | `ample lsp` and the `/lsp` WebSocket: capabilities, transports, measured budgets, decisions and what is not done; the VS Code extension and the generated grammar. |
| [The .ampletx package](ampletx.md) | Portable single-file snapshots of a song (Full, Derived), the manifest, only what the build needs, the read-only viewer and the import. |
| [The AI assistant (Codex)](ai-assistant.md) | Codex proposes, AMPLE validates, you decide: server policy, project identity and mode, the proposal workspace, MCP context and skill. |
| [AI browser tool](ai-browser.md) | Visible Playwright browsing with task/project approval, screenshots, pointer hover, page media mute, aggregate diagnostics and optional page JavaScript. |
| [Saving mode](ai-saving-mode.md) | How a coding agent works when asked to conserve usage: concise narration, safe batching of reads and edits, guarded edit scripts, focused verification. |
| [The web UI (as built)](web-ui.md) | What the web app does today (layout, palette, canvas, ports and links, groups, properties, undo, build), what is not yet there, honest differences from the sketch, how to run the browser tests. |
| [Client-side preview analysis](clientside-preview/README.md) | Block-by-block feasibility and requirements for browser-local previews, including server dependencies and explicit exclusions. |
| [Extending the Studio UI](studio-ui-integration.md) | AMPLE-only guide for new plugins and views, Studio components/tokens, preview honesty, and a quick migration check for concurrent work. |
| [MIDI support](midi.md) | Bounded SMF handling, note rendering, generated modules and nested module navigation. |
| [AMPLE Lite](lite.md) | The small code-first playground at `/lite/`: files, editor, demos, build and play, browser-local storage, Reset Lite, the safety boundary and the licences of its sounds. |
| [Local server and API](api.md) | `ample serve`, the safety rules (loopback, host, origin, token), document concurrency, two-step dangerous acts, events. |
| [Recording your voice](recording.md) | The microphone recorder: record locally, listen, trim, set the latency, then upload one take as an import-audio block. |
| [Instance registry](instance-registry.md) | Per-user server records, stale detection, protected launch URLs, ping and graceful shutdown. |
| [Opening projects](launcher.md) | `ample open`, recent projects, detached server logs and the Windows launcher. |
| [Plugins (as built)](plugins.md) | The plugin manifest, the JSON-RPC protocol version 1 and what the host guarantees: clean environment, job folder, timeouts, crash containment, one GPU job at a time. |
| [Faer bridge (in progress)](faer-bridge.md) | Optional per-project routing for ACE song and voice generation, voice cloning and stem separation to the authenticated Trooper.ai service; GPU acceptance checks remain. |
| [Faer deployment notes](faer-operations.md) | Verified Trooper.ai setup findings, measured install steps, deployment choices and open validation checks. |
| [FaerSpark](faer-spark.md) | The local ACE-Step server on demand: leases, start and stop, the "Faer, else local" route, and the model checks AMP5381, AMP5382 and AMP2014. |
| [Play lights](play-lights.md) | The Blocks view lights up while the song plays: which blocks are audible, where the signal flows, the motion per kind of block, when the lights are allowed. |
| [Templates](templates.md) | The Templates tab: the built-in beats with a wizard (preview, kit, chords and bass), suggestions without a model, and your own templates saved per project. |
| [Koda, the small helper](koda.md) | The eighth note of the logo as a helper: ask about a block, code or a field (Ctrl+K), answers from the template docs, drafts shown dashed on the board, hide / peek / mute, the two lanes beside the assistant. |
| [ACE-Step model catalog for Codex](faer-model-catalog.md) | Live Trooper model inventory shape, ACE model roles, recommendations, quantization trade-offs and exact-file selection rules for Codex. |
| [ACE-Step plugin](acestep-plugin.md) | The plugin for an acestep.cpp server: the assumed server API (unverified), the rules it follows, the stand-in server and the manual test for the real server. |
| [Regeneration safety](regeneration.md) | The warning that AI regeneration is only guaranteed locally, and the safe regen flow: old take set aside, result verified, default "restore previous", crash-safe pending decision. |
| [Determinism](determinism.md) | The four determinism levels, lock/freeze/pin/refresh, retries and checks, how reproducibility is verified. |
| [Render recipes and reproduction checks](render-reproducibility.md) | The generation-input audit, saved recipes/provenance, CFG baselines and comparison of old takes with current software. |
| [Modules](modules.md) | Opt-in modules and plugins, the plugin contract, models, versions. |
| [Bake](bake.md) | Turning dynamic results into data blocks; recipe-exact versus data-exact. |
| [Tech](tech.md) | C# core, local server and web UI, stable PRNG, toolprints, project folder, build order. |
| [Variants](variants.md) | Making many takes of a block, scoring them 0-100, picking and locking one, keeping a shortlist for later. |
| [AMPLE Redux journal](journal.md) | The append-only recovery journal, durable AR references, recovery model and retention; the user-facing name is AMPLE Redux. |
| [Controllability](controllability.md) | What the producer can pin, tweak and expect from an AI block: the freeze ladder (seeds, melody plan, take) and the "if I change X" table, with measured versus expected marked. |
| [Library manager](library-manager.md) | Everything a song uses (catalogs, packs, voices, plugins, templates, data), where it is used, replace with impact analysis, folders, global catalogs as linked folders. |
| [Language support](language-support.md) | Diagnostics (codes, ranges, hints, quick fixes), the linter and its rules, CLI and language server, a generated language reference that cannot drift. |
| [Source and views](source-and-views.md) | One song, three views: node editor, editable arrangement, source editor with validation; Apply versus Save; mock syntax and its checks. |
| [How a song is made](how-a-song-is-made.md) | How linked blocks become a song: typed values, the shared timeline, where sync is exact and where it is measured, what a build does. |
| [Project management](project-management.md) | In detail: project folder and `.ample/` layout, lifecycle (create, open, duplicate, archive, delete), templates, settings scopes, health checks, backup and restore, migration, the dangerous-act pattern. |
| [History management](history-management.md) | In detail: data model, operations and their risk, the History screen, compare, search, scenarios, storage, integrity, CLI twins. |
| [History v1](history.md) | Branches and "promote" instead of merges, the archive, sealed releases, the git mirror. |
| [Release](release.md) | Release criteria, measured baselines, packaging, the open items before v1.0. |
| [Open points 2026-10-10](open-points-2026-10-10.md) | What is open after the long session of 2026-10-10: gaps, failures that were already there, voice clone and GPU, the AI assistant, and the design for moves proposed by Codex. |
| [Security review](security-review.md) | The checklist of defences with the tests that prove them, the dependency audit, what is not verified. |
| [The guide](../guide/README.md) | The learning help with pictures and a dictionary (open it with the Guide link in the app, or at /guide/); how it is made and kept up to date; [the doc style](../guide/STYLE.md) holds the house style rules. |
| [Library bundles](bundles.md) | Save blocks with their files, seeds and audio in the library and place them in another song under new names. |
| [Sequencer](sequencer.md) | The advanced sequencer block: channels, patterns, a playlist and lanes in a .seq file, and its workspace. |
| [Assistant tools](assistant-tools.md) | The tools of the assistant that are not AI: the beats catalogue, audio analysis, arrangement lint, mix hints and pattern generators. |
| [Samples](samples.md) | Real instruments: sample drum kit and guitar blocks, sample sets, pattern and strum grammar, words from a file. |
| [Sound sources](sound-sources.md) | A researched list of sample and sound effect sources and their licences for a commercial streaming release (2026-10-08, not legal advice). |
| [Development setup](dev-setup.md) | Build AMPLE, run the focused tests and use the development tools. |
| [Test parallelism](testing-parallelism.md) | Current safe parallel groups and shared-state suites to keep serial or isolate first. |
| [Analysis](analysis.md) | Tempo, key, loudness, spectrum, pitch and melody analysers, the analysis plugin contract, tolerances and caching. |
| [Security](security.md) | The vault, signing and optional encryption: what is protected, what is not, key loss, the crypto used. |
| [Backup](backup.md) | What a backup zip contains, profiles, secrets (sealed inside or a sibling file), restore without retyping. |
| [Jargon](jargon.md) | The studio/music vocabulary AMPLE would use as keywords. |
| [Future ideas](future.md) | Ideas tracked on purpose but left out of v1 (smarter merges, adopt block, A/B merge, ...). |
| [Open questions](open-questions.md) | The questions still to be decided. |
| [Clickable sketch](sketch/README.md) | One HTML file (plain JS, open it in a browser) that is also a knowledge base: ComfyUI-like canvas with groups and minimap, history and structure browser, variants, build with cache and preview playback, linked folders. Machine-readable notes inside (see its README). Mock only. |
| [Mockup parity matrix](parity-matrix.md) | Every sketch feature mapped to delivery chunks, a plain-language scenario and eventual executable proof. |
| [CLI session (mock)](cli-session.md) | What the commands could look like (invented output). |

## Example files (`examples/`)

[`17-splitter-branches`](examples/17-splitter-branches/) duplicates mono drums into independent EQ and delay/reverb branches, with volume/channel controls and a stereo mix.
[`25-piano-roll-tour`](examples/25-piano-roll-tour/) is a sample-free editable clip for exploring piano-roll tools and per-note properties.
`01-hello` instruments only · `02-hybrid` instruments + AI voices + overlap + booth directions · `04-remix` importing audio, MIDI, old recipes, stems · `05-lyrics` timed words, checks, queries, retry policy · [`16-audio-vs-render`](examples/16-audio-vs-render/) compares full effect chains on materialized audio and render recipes · [`21-fades`](examples/21-fades/) a crossfade between two drum patterns, a fade out as a formula and a fade block in seconds · [`22-sections`](examples/22-sections/) named drum patterns put in order · [`23-sequencer`](examples/23-sequencer/) a sequencer block with channels, patterns, a playlist and lanes · [`24-tutorial-song`](examples/24-tutorial-song/) a full band song (sequencer, sample kit, guitar, a General MIDI choir and effect chains), built step by step in the guide · `packs/` soundset manifests · `plugins/acestep/plugin.json` · `ample.lock`.
| [Third-party credits](third-party-credits.md) | What AMPLE learned from or uses that is not its own (MonkSynth for Delay Llama under MIT, the Delay Lama as inspiration, a CC0 demo voice), with licence and how it is used. |
