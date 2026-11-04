# MIDI notes editor parity scenario

1. Open a MIDI clip with a compact `notes` line from the node property editor and from the source gutter pencil.
2. Inspect named `bar`, `start`, `length`, `pitch`, `velocity`, routing and tie fields; reject invalid values without applying.
3. Apply edits, preserve neighboring comments, undo and redo, then save and reopen the module.
4. Open a generated Queen-sized fixture and confirm the modal stays responsive without one DOM row per note.

Generated language and Node fixture tests cover exact integer PPQ conversion, validation and large input paging. Browser parity remains pending.
