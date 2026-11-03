> Status (2026-10-03): the language server of section 6 is built (chunk 80); what it does today and what is still open is in [lsp.md](lsp.md).

> The normative language specification is now in [language/](language/README.md); this page is background. Status (2026-10-02): design sketch from a brainstorm. Nothing here is built. The clickable sketch (`sketch/index.html`) mocks the parts marked "in the sketch": diagnostics with positions and quick fixes, a linter, completion, CLI-style output and a generated language reference.

# Language support: compiler diagnostics, linter, editor tooling, docs site

A language people write by hand needs more than a parser. It needs **good errors**, a **linter**, **editor support** and **documentation that cannot drift**. All of them hang on one compiler front end, so a message looks the same in the editor, the terminal and a CI log.

## 1. One front end, many tools
```
text (.amplet) ──► lexer ──► parser (error-recovering) ──► syntax tree with source ranges
                                              │
                          semantic analysis (names, ports, types, parameters, cycles)
                                              │
                         linter (rules, configurable) ──► diagnostics ──► editor / CLI / CI / UI
                                              │
                                        apply to the working copy (graph)
```
- **Error recovery:** the parser keeps going after an error (it resynchronises at the next block header or `}`), so one mistake does not hide the rest and the node editor can still show everything that parsed.
- **Source ranges everywhere:** every token and node knows its file, line, column and end, in one convention (1-based lines and columns, columns in characters; the language server converts to UTF-16 for editors that need it).
- **Pure and fast:** parsing and checking a whole song takes milliseconds, so the editor can check on every pause.

## 2. The diagnostic record
Every message has the same shape (in the sketch: shown in the problems list, the gutter, the squiggles and the CLI output):
| Field | Meaning | Example |
| --- | --- | --- |
| `code` | stable identifier, never reused | `AMP2003` |
| `severity` | `error` (blocks apply), `warning`, `info` | `error` |
| `file`, `line`, `column`, `endLine`, `endColumn` | the exact range | `song.amplet:43:3-9` |
| `message` | one sentence saying what is wrong | `Unknown parameter "patern" for drums.` |
| `hint` | what to do, often with the valid choices | `Did you mean "pattern"? Known parameters: kit, pattern, bars` |
| `related` | other places that matter | `first used here` pointing at line 12 |
| `fixes` | zero or more quick fixes with a label and an edit, marked `safe` or `risky` | `Replace with "pattern"` |
| `explain` | link to the long explanation | `ample explain AMP2003` |
Rules for the messages themselves: say what is wrong and where, in plain words, without jargon; point at the smallest range that is wrong (the parameter name, not the whole line); suggest the likely fix; never blame.

## 3. Codes
- **`AMP1xxx` syntax** (the text is not shaped right): missing composition line, malformed header, bad `pinned@version`, bad placement, unclosed block, bad `from` or parameter line.
- **`AMP2xxx` meaning** (the text is shaped right but says something impossible): unknown block type, duplicate block name, unknown parameter, no such block, no such output or input, type mismatch, input with two sources, a block reading itself, cycles.
- **`AMP5xxx` environment** (depends on what is installed or reached): a plugin is not installed, a link is unreachable. Usually warnings.
- **`Lxxx` lint rules** (see section 4).
- **Plugin-provided codes** use the plugin's prefix (`ACE1xxx`), declared in its manifest, with the same record shape.
Codes are stable across versions; a code is never reused for something else. A retired code stays in the catalogue as "no longer produced".

## 4. The linter
Lint rules find things that compile but are probably not what you meant. Each rule has a code, a name, a default level (`off`, `info`, `warning`, `error`), an explanation with a wrong and a right example, and optionally a quick fix.
| Rule | Name | Default | Finds |
| --- | --- | --- | --- |
| L001 | unused-output | info | a block whose outputs nothing reads |
| L002 | missing-input | warning | a required input without a source |
| L003 | lock-and-refresh | warning | a block that is locked and also always-refetch |
| L004 | overlapping-parts | warning | two parts of one block overlap in time |
| L005 | dynamic-not-fixed | off | a dynamic block neither baked nor locked (results can change) |
| L006 | pinned-outdated | warning | a pinned block older than its template (the fix changes the sound, so it is `risky`) |
| L007 | no-path-to-master | warning | a block that makes audio but never reaches the master |
| L008 | name-case-clash | warning | two block names differing only by case |
| L009 | custom-seed | info | a dynamic block uses custom seeds instead of derived ones (fine and recorded; changing them later breaks reproduction of takes made with the old ones) |
**Configuration:** per rule in the project settings, saved in the song as one line (`lint { L004 error  L005 info }`, only entries that differ from the default). A rule raised to `error` blocks Apply like any syntax error. **Suppression:** a comment above a block, `# ample-ignore L004, L005`, silences those rules for that block only (and the linter can warn about ignores that no longer match anything). In the `.amplet` source editor, right-click a lint diagnostic and choose **Disable Lxxx for this block** to add the comment. Release levels (see the quality module) can turn selected rules into errors for a release.
More rules to consider later: a part longer than the whole song, a tempo that disagrees with an imported audio file, a baked block whose recipe is no longer valid, credits that do not sum to 100%, an external data source with `refresh` in a sealed release.

## 5. Quick fixes and "Fix all"
A fix is a text edit tied to a diagnostic (replace a range, insert a line, append). `safe` fixes do not change meaning (typo replacement, add a missing `}`, clamp a value, remove a contradictory flag) and are applied together by **Fix all**; `risky` fixes change what the song sounds like (updating a pinned block) and are only ever applied one by one, on purpose. Fixes are applied bottom-up so positions stay valid.

## 6. Tooling
```
ample check song.amplet [--format text|json|sarif] [--lint]      validate; exit code 1 on errors
ample lint  song.amplet [--fix]                                    lint rules; safe fixes with --fix
ample fmt   song.amplet                                            normalise the text
ample explain AMP2003                                              the long explanation of a code
ample docs  [--out docs-site/]                                     generate the language reference as a static site
ample lsp                                                          language server (stdio)
```
- **Output formats:** human text (file:line:col, severity, code, message, a source excerpt with carets, hint, fix, `more:` line), JSON (one record per diagnostic, as in section 2) and SARIF for CI code-scanning integrations.
- **Language server (LSP):** live diagnostics with ranges, **hover** (block type, ports, parameter docs, current cache state), **completion** (block types, parameters, block names, outputs after `from x.`, inputs after `->`, plugin-provided types), **go to definition** (from a `from` line to the block), **find references**, **rename** (a block and every reference), **quick fixes**, **code lens** (baked? cached? how many variants?), **formatting**, **semantic tokens**. VS Code first; any LSP client works. The web editor in AMPLE uses the same engine over its API.
- **In the sketch:** Check, Apply, Fix all, the problems list with excerpts, hints and "Explain", squiggles and gutter markers, `Ctrl+Space` completion, the CLI output dialog (text and JSON) and the reference.

## 7. Documentation that cannot drift
The language reference (the "language support site") is **generated**, not hand-written:
- **Block types:** from the templates (ports with types, parameters with defaults, update class and version, required plugin, an auto-built example). A template or plugin update regenerates its page.
- **Syntax, ports and types:** from the grammar and the type table.
- **Diagnostics catalogue:** from the code table (title, explanation, wrong example, right example), so `ample explain AMP2003`, the editor's Explain button and the site show the same text.
- **Lint rules:** from the rule table, with the level selectors that edit the project's `lint` line.
- **Versioned:** one site per language version; template and plugin pages carry their own versions; old versions stay readable.
- **Tested examples:** every example in the docs (the wrong ones must produce the stated code, the right ones must compile cleanly) is compiled in the test suite, so an example can never rot.
- **Searchable and linkable:** every block type, parameter, code and rule has a stable URL; diagnostics link straight to their page.
- **Offline:** the app ships the generated site; no network needed (the sketch's Reference button is this, as an overlay).

## 8. Quality of the diagnostics themselves
- **Golden tests:** a folder of small broken songs, each with the expected diagnostics (code, range, message) in a file next to it; any change in a message shows up as a diff in review.
- **Fuzz and recovery tests:** random truncations and deletions of valid songs must never crash the parser and must keep reporting sensible errors.
- **Message reviews:** wording changes are reviewed like code; the catalogue keeps a one-line "why" per code.
- **Performance budget:** a check of a large song stays under a fixed time budget (tracked in CI).

## 9. Relation to the rest
- **Apply and leaving the source editor** need zero errors (see [Source and views](source-and-views.md)); warnings and info never block.
- **Plugins** contribute block types (so the reference, completion and checks know them) and may contribute diagnostics and lint rules; a missing plugin is `AMP5001`, never a silent skip.
- **Quality module:** release levels can promote lint rules to errors for a release; the quality scorers are separate (they measure sound, the linter reads text).
- **History:** diagnostics are not stored in revisions; a revision is only made from a song that compiles.

## 10. Open questions
1. Should the formatter keep comments and blank lines exactly (lossless), or is a normalised text acceptable?
2. Should lint levels also be settable per release level in the same `lint` line?
3. Do plugin-provided lint rules need a permission or a pinned version like plugins themselves?
4. Is SARIF worth the effort in v1, or are text and JSON enough?
5. How are diagnostics for the inner graphs of groups and templates shown, if those are node-editor-only?
