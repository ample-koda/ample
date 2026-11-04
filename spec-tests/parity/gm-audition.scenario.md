# GM instrument audition parity scenario

1. Select a General MIDI instrument and change its bank, program, channel, gain or limiter in the Parameters property tab.
2. Preview a test note, then preview the first 128 notes of its connected MIDI clip, confirming source channel/bank/program metadata is preserved and channel `-1` keeps all channels.
3. Confirm the shared Stop button stops and rewinds the preview, and the project creates no take or revision.
4. Repeat with a drum channel and an unavailable preset; inspect fallback warnings and listen for the owner acceptance check.

Executed and passing (2026-10-04): the generated-note renderer tests and the `/api/gm/audition` route test (Server tests). Not yet verified: browser playback, no-take interaction and owner listening to real presets (human review).
