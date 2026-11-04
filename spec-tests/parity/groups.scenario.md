# Groups parity scenario

1. Select linked blocks and group them, including links crossing the selection boundary.
2. Collapse, expand and enter the group, then return through the breadcrumb.
3. Ungroup it and expect the same blocks, links and source semantics as before grouping.
4. Open a generated MIDI module from its instance node, reload the song, and confirm the nested graph and breadcrumb path are restored from project data.
5. Open a module containing tempo events and verify its clip, GM instrument and mixer remain editable while the banner identifies preserved event facts; edit each node, exercise undo/redo inside the module, save, and confirm event text remains in module source. Return to the song and confirm the module's undo history is not applied to the song.
