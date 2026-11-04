# Project panel parity scenario

1. Open the bottom Project tab and confirm the project root is selected and its files are shown, even if another folder was open in a previous visit.
2. Drag a folder in the tree onto another folder and confirm the folder moves inside it; dragging a folder onto itself or one of its descendants is ignored.
3. Search and filter files, then switch between tiles and the detailed list.
4. Sort by a column and change tile size; reload and verify the layout is remembered.
5. Reveal a nested file, open it by type, and confirm server changes appear through `files.changed`.
6. Use upload and the context menu for file actions; verify mutations are journaled and recoverable.
7. Focus the tree and collapse/expand its project root with Left and Right; use Up/Down to move among visible folders.
8. In Details, use the column separator's Arrow keys to resize, drag headers to reorder, and right-click a header to add/remove optional columns; confirm changes persist to the shared project setting.
9. Load a folder with more than 2000 entries in Details; confirm only a visible window is rendered, scroll near the end, and use arrow/Home/End to reach and select off-screen rows.
