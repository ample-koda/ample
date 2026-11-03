# 09 Formatting

The output of the formatter is **the canonical form**. `ample fmt` rewrites a file to it; the language server's formatting does the same; the corpus (`../../spec-tests/format/` pairs and `roundtrip/` files) defines it by example.

## Rules (spec 0.1)
1. UTF-8, `\n` line endings, **no trailing whitespace**, exactly one final newline; a BOM is removed.
2. **Indentation** is two spaces inside blocks; tabs are replaced.
3. **Blank lines:** a run of blank lines collapses to one; blank lines inside a block are kept as one.
4. **Single spaces** between words in a header, between a parameter name and its value, and around `->`; no space before `,` and one after it.
5. **Brace lines** (`project`, `features`, `lint`) read `keyword { pair  pair }`: one space inside the braces, **two spaces** between pairs.
6. A block header ends with ` {`; the closing `}` is alone on its line.
7. **Comments are preserved** exactly, except that trailing whitespace is removed and the line is indented to its context (no indent at top level, two spaces inside a block). A comment after code on the same line is not part of the language (it would make the line unreadable to the parser).
8. The formatter **never reorders** statements, parameters or `from` lines, and never changes values or names.
9. The formatter is **idempotent**: formatting its own output changes nothing, and parsing then printing a canonical file gives the same bytes (round trip).

## Behaviour on broken input
The formatter formats everything it understands and leaves a line it cannot parse **unchanged** (besides trailing whitespace and indentation inside blocks). It never refuses.
