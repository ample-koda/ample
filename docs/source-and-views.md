> Status (2026-10-02): design sketch from a brainstorm. The syntax below is the MOCK syntax used by the clickable sketch (`sketch/index.html`); it is not a final language design, but it is real enough that the sketch parses, validates and applies it.

# One song, three views: node editor, arrangement, source

The song is **text** (`song.amplet`). The **node editor**, the **arrangement** and the **source editor** are three views of that text. Edit in any of them and the other two follow.

## 1. The three views
| View | Good for | Writes to the text |
| --- | --- | --- |
| **Node editor** (blocks) | structure: what feeds what, parameters, groups, templates | blocks, parameters, `from ...` lines, layout |
| **Arrangement** (timeline below) | time: where each block sounds | the `at <bar> for <bars> [as <name>]` part of the block header |
| **Source** (text) | precision, bulk edits, copy and paste, reading | everything |

## 2. The arrangement is editable
- **Drag a bar** to move it, **drag its edges** to resize (snaps to whole bars). Several parts per block are allowed (a piano with `verse` and `chorus`).
- **Double-click a bar:** go to that block's source line. **Double-click empty space** on a track: a new 4-bar part. **Right-click:** go to source, select in the node editor, rename the part, duplicate, split, delete.
- Blocks without a placement can be added with the dropdown above the timeline.
- An edit changes the text (and the cache keys: a moved block is a changed block, so it and what depends on it turn grey for the next build) and shows the working copy as **unsaved**.

## 3. The source view
Switch the canvas area from **Blocks** to **Source**.
```
┌ files ────────────┬ song.amplet                     [Apply] [Revert] [Show block in node editor]   status ┐
│ ✎ song.amplet  ●  │  1  # song.amplet ...                                                                  │
│ ✎ lyrics/chorus   │  2  composition "my-song" { ample 1  tempo 92  master-seed 424242 }                                 │
│ ✎ chords/main     │ ... editor with line numbers; ● error and ▲ warning markers in the gutter              │
│ 🔒 ample.lock      ├────────────────────────────────────────────────────────────────────────────────────────┤
│                   │ problems: ● line 43: Unknown parameter "patern" for drums. Did you mean "pattern"?     │
└───────────────────┴────────────────────────────────────────────────────────────────────────────────────────┘
```
- **Files on the left:** `song.amplet` (the graph), plain text files the blocks refer to (lyrics, chords), and generated read-only files (`ample.lock`). Only `song.amplet` changes the node editor.
- **Live validation** while typing (a short pause), with a problems list; click a problem to jump to the line.
- A valid song edit syncs to the **working copy** (graph, arrangement and other views) after a short pause. **Save** makes the revision; Ctrl+Enter confirms the current buffer as applied.
- **Revert** drops invalid or not-yet-synced edits and returns to the current working copy.
- While the text cursor is inside a block in a valid song, the Properties panel shows that block. Source and property edits sync both ways; while the code has errors, the properties panel explains that sync is paused until the code is valid again.
- **Show block in the node editor** jumps to the block under the cursor (needs a valid song).
- **Leaving the source view** (to Blocks or the Branch manager) **applies first; if there are errors the switch is refused** with a message. A valid song switches back and every other view updates.
- **A project that opens with mistakes starts in the source view** and stays there until the errors are fixed (or reverted). In the sketch, open *always-remix* in the Projects manager to see it.
- Dirty non-song source files can be closed by saving, keeping a recovery draft, discarding or cancelling. Recovery drafts are offered when each file is next opened; a manual checkpoint saves pending source drafts first and stops if a file changed since its draft was made.
- When safe saves are enabled, recovery saves in that manual checkpoint batch skip their individual auto-checkpoints; the final manual checkpoint remains the single snapshot for the batch.
- File writes and recovery records are journaled in AMPLE Redux. Its current on-disk folder remains `.ample/blackbox` for compatibility; compaction is explicit and uses the project budget.

## 4. Mock syntax
```
composition "my-song" { ample 1  tempo 92  master-seed 424242 }
features { quality off  credits off  signing off  secrets on  lyrics on  git-mirror off  assist diff }

drums drums at 1 for 16 as "beat A" {
  kit kit/tape
  pattern beat-A
  bars 16
}
piano piano pinned@2.0 at 1 for 8 as verse, 9 for 8 as chorus {
  set felt-piano
  from chords.notes -> notes
}
ai-voice chorus_vox at 9 for 8 as "chorus voice" {
  voice anna
  from lyrics.words -> lyrics
  from chords.notes -> melody
  from weather.data -> style
}
layout drums 280 20
```
Header: `<block type> <name> <class>@<version> [at <bar> for <bars> [as <name>] (, ...)] [bake] [lock] [refresh] {`. Body: `<parameter> <value>` lines and `from <block>.<output> -> <input>` lines. Lines starting with `#` are comments. `layout` lines (at the end) keep node positions. Names with spaces or odd characters are written in quotes. The inner graph of a group or of a block like the AI voice is edited in the node editor; the text shows a comment line for it.

## 5. What validation checks
| Check | Message idea | Blocks leaving? |
| --- | --- | --- |
| Project line present, tempo 20 to 300, seed an integer | "Tempo must be between 20 and 300." | yes |
| Unknown block type, class or version malformed | "Unknown block type 'drumz'. Did you mean 'drums'?" | yes |
| Unknown or duplicate parameter | "Unknown parameter 'patern' for drums. Did you mean 'pattern'?" | yes |
| Duplicate block names | "Two blocks are called 'bass'." | yes |
| `from` points to a missing block, output or input | "There is no block named 'chordz'. Did you mean 'chords'?" | yes |
| Type mismatch between a connected output and input | "Type mismatch: x.vocal is audio but y.lyrics wants text." | yes |
| An input with two sources, a block reading itself | "Input 'in2' of 'mixer' already has a source." | yes |
| Cycles | "Cycle: a → b → a" | yes |
| Unclosed block, bad header, bar numbers below 1 | "Block 'x' is not closed (missing '}')." | yes |
| A block needs a plugin that is not installed | "Block 'x' needs the plugin 'timing', which is not installed (opt-in)." | no (warning) |
| A part reaches past bar 1024 | warning | no |

## 6. How Apply maps text to the graph
- Blocks are matched **by name**: the same name and type keeps its identity, its inside (groups, templates) and its cache state; a new name creates a block from the template; a block that is gone from the text is removed (the number is reported).
- Parameters, class, version, parts and flags come from the text; missing parameters get the template default; links are rebuilt from the `from` lines.
- Positions come from the `layout` lines, or a free spot for new blocks.
- Applying text that equals the current graph changes nothing (cache keys stay equal).

## 7. Interaction with history
- Apply and arrangement edits change the **working copy**; the **unsaved** badge compares the text with the last saved text. **Save** makes a revision (and clears the badge). Going to another revision with unsaved work takes an automatic checkpoint first (see [History management](history-management.md), "Unsaved work").
- Errors never reach a revision: a revision is only made from a valid song.

## 8. Open questions
1. Should text edits made in another editor (VS Code) while AMPLE is open be picked up (file watcher) and validated the same way?
2. Should the inner graphs of groups and templates be editable in the same text (nested blocks), or stay node-editor-only?
3. Should comments and formatting be preserved when the text is regenerated from the graph (a lossless round trip), or is a normalised form acceptable?
4. Should the source view offer completion and inline docs (block types, ports, parameters) from the templates?

## 9. Dedicated editors (lyrics)
Some blocks deserve a real editor of their own. The first one in the sketch is the **lyrics editor**.
- **Open it** with the **…** button next to the `file` parameter, **Edit lyrics** in the block menu, the **… Edit lyrics** button in the properties, or a **double-click** on the block.
- It **takes over the canvas area** (full screen in the editor area) instead of showing the node view. The file the block points to (`lyrics/chorus.lyr`) is the data: one line per row, `[bar 9] text`.
- **Rows:** bar or seconds (switch above), text, the other unit as a read-only column, move up or down, insert after, delete. A bar can be fractional (`9.5`).
- **Checks, live:** first line within 5 s (the block's `first5s` setting), lines in order, lines outside the block's placement, empty lines, long lines, too many words for the gap. A **timing strip** shows where the lines sit inside the placement and colours the problems.
- **Import / export:** paste `.lrc` or `.srt` (times become bars at the project tempo), export `.lrc`.
- **Undo and Redo, one step at a time** (Ctrl+Z, Ctrl+Y outside text fields; the buttons show how many steps are left) work for edits, moves, inserts, deletes, imports and sorting.
- **Sort by time:** a button orders the lines by bar (stable: lines on the same bar keep their order) and is only enabled while the lines are out of order. The check panel offers the same fix next to an order error, and **auto-sort on apply** (on by default) sorts silently when you press Apply and back.
- **Linking the views:** hovering a mark in the **timing strip** highlights the matching row (and scrolls it into view); hovering a row or a check message highlights its mark in the strip, and **editing** a row (focusing its bar or text field) keeps its mark highlighted in a stronger colour while you type, so the mark moves live when you change the bar.
- **Two ways back:** **Apply and back** (keeps the changes in the working copy, makes **no** revision, refused while there are errors) and **Undo and back** (throws the session's changes away, with a confirmation if anything changed).
- The same pattern is meant for other blocks later (chords, drum patterns, a mixer, an automation lane): a block can offer a dedicated editor, always with Apply and back / Undo and back.

## 10. Ctrl+click navigation and read-only
- **Hold Ctrl (Cmd):** in the source editor the elements that lead somewhere light up: block names (in headers and in `from` lines), both ends of a connection, a placement (`at ...`), a lyrics `file`, a block type. The cursor becomes a pointer over them and a label says what a click does.
- **Ctrl+click** opens the node editor on that element and selects it. On the `->` arrow of a `from` line, it selects and highlights the represented connection, then opens that connection's context menu. A placement also shows the arrangement, a lyrics file opens the lyrics editor, and a type opens its page in the language reference.
- **With errors in the text** a popup asks: **Fix first** (stay, jump to the first error) or **Show read-only**. Read-only shows the **last valid version** of the node editor, with the element selected if it exists there ("does not exist in the last valid version" otherwise). A banner says so and offers **Back to the editor**.
- **Read-only means:** no moving, connecting, adding, deleting, grouping, parameter or arrangement edits; selecting, zooming, opening groups and going to the source still work. It stays read-only **until you go back to the source editor**; entering the source view ends it, and getting back to the node editor then needs a valid song again (or the same popup).
- The plain **Blocks** button uses the same popup when the text has errors.

## 11. The lock file (`ample.lock`)
`ample.lock` is **generated and read-only**. It pins everything the song depends on that is not written in the song text, so the song reproduces later: plugin versions with their **toolprints**, soundset and sample packs by **hash**, templates and pinned blocks by hash, locked external data (the recorded weather value, when it was fetched, its hash) and model files. It plays the role of `package-lock.json`: the text says what you want, the lock says exactly what you got. It is part of every revision, changes only when you update a plugin, a pack or a pin, or lock external data, and `verify` and `upgrade-check` compare the present setup with it. See [Determinism](determinism.md) and Tech.
