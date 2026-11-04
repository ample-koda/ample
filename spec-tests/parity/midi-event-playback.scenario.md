# MIDI event playback parity scenario

1. Import a code-generated MIDI module with tempo changes, channel volume/expression, sustain and pitch bend events.
2. Build the module and its connected General MIDI instrument twice from the same source and assert identical audio bytes.
3. Edit a retained event in the unsaved module source, build again, and assert the instrument take is rebuilt with the edited event.
4. Confirm unsupported pressure, vendor and system events remain in the MIDI source/export and appear in the build warnings.
5. Confirm event-to-sample mapping uses integer tempo ticks and the channel-10 track continues to route through the percussion bank.

## Status (2026-10-04)
- Executed and passing: Core MIDI and sampler tests (steps 2 and 5, integer tick tempo, channel-10 routing).
- Written, not yet executed in a browser: steps 3 and 4 as a dedicated browser assertion (edited events changing the audio build).
- Human review: listening to the Queen audition and the M24 browser assessment.
