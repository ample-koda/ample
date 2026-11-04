# File picker parity scenario

1. Open the project picker with a current value and verify it is selected and visible.
2. Choose a file and verify the picker returns its project-relative path.
3. Search by name, filter by accepted extension, upload to the root and a nested folder, and inspect duplicate handling.
4. Use no file and verify the result is the empty string; cancel and verify no value is returned.
5. Create a folder, upload a file, then upload it again and choose Keep both; verify the timestamped second file appears. Repeat with each conflict choice and Apply to all.
6. Expand and collapse folders with the arrow, double click, Left and Right keys; use Backspace to move to the parent and `/` to focus search.
7. Navigate folders using keyboard controls and confirm Escape closes the dialog.
8. Upload one or more files and verify each row exposes byte progress and a final complete or skipped state; resolve a duplicate and verify progress continues through the choice.
9. Populate a folder with more than 2000 entries and verify the visible list stays windowed while scrolling can reach and select entries near the end.
10. For a file field, Open a project audio file and verify the shared player uses its relative path; open linked MIDI and lyrics files and verify their existing block views are selected; open a text file and verify Source shows that file.
11. Clear a file field, inspect the working copy, and verify the parameter line is removed; confirm Undo restores it and a read-only field disables the controls.
