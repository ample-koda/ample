> Status (2026-10-03): built in chunk 80. The fresh VSIX was installed and exercised in an isolated VS Code 1.140.0 profile on Windows.

# The language server (as built)

`Ample.Lsp` is a Language Server Protocol 3.17 server for `.amplet` songs. It holds **no language logic**: every answer comes from `Ample.Language` (parser, queries, formatter, edits), so the editor, the CLI and the web UI cannot disagree. A test runs the server over the whole conformance corpus and compares its diagnostics with the library's.

## Using it
| How | What |
| --- | --- |
| `ample lsp [--root <ample folder>] [--help-base <url>]` | Stdio transport (Content-Length framed). Editors start this. Nothing else is written to stdout. |
| `ws://127.0.0.1:<port>/lsp` of `ample serve` | WebSocket transport: one JSON-RPC message per text frame. The browser's session cookie is the token; the same host and origin checks as the API apply. |
| `editors/vscode/` | The VS Code extension: starts `ample lsp`, TextMate grammar, snippets, language configuration, commands. |

## What it does
| Capability | Notes |
| --- | --- |
| Text sync | Incremental; positions are UTF-16 (`positionEncoding: utf-16`). |
| Diagnostics | Pushed on open, change, save; pull (`textDocument/diagnostic`) too. Each carries the code, a `codeDescription.href` to `<help base>#AMPxxxx`, the hint in the message and the related line. |
| Hover | Block type, block, parameter (from the spec data). |
| Definition, references, rename (with prepare) | A block and every `from` reference. |
| Symbols, folding, selection ranges | Document symbols (blocks), workspace symbols over the open documents, folding of blocks, selection from word to line, block and document. |
| Completion | Block types as snippets (class, version and parameters filled in from the spec), parameters, the values of a parameter with fixed choices, block names, ports after `from x.` and `->`, classes, `at` and `for` after the tag, the `# view group/size/pin` lines with block names, keywords. The studio editor also shows the best guess in grey while typing (Tab takes it; Esc hides it until another line or a space) and Ctrl+Space opens the list. |
| Signature help | The parameters of the block type around the cursor. |
| Formatting | Whole document (a range request formats the whole document too, because the formatter works on whole songs). |
| Code actions | Every quick fix of the library, "Fix all", "Bake" and "Lock" for the block under the cursor. |
| Semantic tokens | Full and range: block types, names, parameters, flags, classes, numbers, strings, comments. |
| Inlay hints | The type of each connection after a `from` line. |
| Cancellation, order | Requests are handled one after the other, so results are in order; a cancelled request that has not started is dropped (error -32800); after `shutdown` requests are refused; `exit` gives code 0 after a clean shutdown, 1 otherwise. |

## Measured budgets (2026-10-03, Windows 11, one developer machine, Debug build)
| What | Budget | Measured |
| --- | --- | --- |
| Diagnostics after an edit in a song of more than 5,000 lines (parse, analyse, publish) | 150 ms | 52 ms |
| Completion in the same song | 50 ms | under 1 ms |

The numbers come from `LspTests.Budgets_for_a_five_thousand_line_song`, which fails above 1 s and 500 ms; the budget is checked by reading the printed numbers. A Release build and other machines were not measured.

## Decisions
- **No OmniSharp, no StreamJsonRpc.** The protocol is a thin layer over `System.Text.Json` (about 700 lines): no dependency, nothing to keep in step with a library, and the in-memory channel makes every capability testable without a process.
- **The grammar and the snippets are generated** from `spec/` by `tools/gen-editor-assets.mjs`; `--check` is part of `check-all`. Grammar tests (`editors/vscode/test/grammar.test.mjs`) use `vscode-textmate` with the same regex engine as VS Code.

## Not done (and where it goes)
- Code lens, document links, on-type formatting, semantic tokens delta, declaration and type definition: not built (the language has few of the things they need).
- Cross-file references and a project folder are not used: the language has no includes yet, so every document stands alone. The read-only fallback for sources outside the project comes with the editor chunk (77).
- Hover does not show the seed lock state or take information, and there are no "extract to group" or "unlock seed" actions (groups are not in the song file yet; seeds live in the project, not the song).
- Diagnostics are pushed on every change (no debounce); at the measured speed this is fine.
- The VS Code integration test packages and installs the VSIX, activates the extension, checks its commands, and sends a completion through the language client to a deterministic local LSP fixture. The real CLI also passed a separate stdio initialize/shutdown smoke check on 2026-10-03. No second client (Neovim or Helix) was tested.
- Diagnostic columns are code points in the library and UTF-16 units in the protocol; the server converts them (tested with an astral character).
