# AMPLE for VS Code

Language support for `.amplet` songs. It starts the AMPLE language server (`ample lsp`, installed with the command line tool) and adds the TextMate grammar and the snippets.

- **Generated:** `syntaxes/amplet.tmLanguage.json` and `snippets/amplet.code-snippets` are generated from `spec/` by `node ample/tools/gen-editor-assets.mjs`; `--check` (part of `check-all`) fails when they are out of date. Do not edit them by hand.
- **Server:** `ample.server.command` and `ample.server.args` (default `ample lsp`).
- **Package:** `npm ci && npm run package` in this folder builds the `.vsix`.
- **VS Code smoke test:** with VS Code 1.85 or newer installed, set `AMPLE_VSCODE_EXECUTABLE` to its executable and run `npm test`. The test packages and installs a fresh VSIX into a temporary profile, activates it and verifies a language-client completion against a deterministic local LSP fixture. It does not download VS Code or contact a model server.

What the server offers: diagnostics (with a link to the help for each code), hover, completion with snippets, signature help, go to definition, find references, document and workspace symbols, folding, selection ranges, formatting, rename, quick fixes, fix all, bake and lock actions, semantic tokens, inlay hints (the type of each connection).
