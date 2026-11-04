# MIDI drag/drop import

Sketch status: designed · Chunk: M11

1. Open a writable song at its root canvas and drop a code-generated `.mid` file.
2. Confirm the modal blocks the canvas, choose a track index and start import.
3. While the upload is delayed, verify a visible grey, non-interactive placeholder.
4. Verify a `midi` block appears with the generated project asset path and chosen track.
5. In a nested module level, verify a file drop is rejected without an upload or graph edit.
