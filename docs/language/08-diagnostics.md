# 08 Diagnostics and lint

The catalogue of codes is data: `../../spec/diagnostics.json` (code, severity, title, explanation, wrong and right example, since, stage) and `../../spec/lint-rules.json`. The generated help renders every code on its own anchor (`help.html#AMP2003`); `ample explain AMP2003` prints the same text. This page defines the rules around them. Background: language-support.

## The record
Every diagnostic has: `code`, `severity` (`error`, `warning`, `info`), a range (`line`, `col`, `endCol`, 1-based, one line), `message` (one plain sentence), optional `hint`, optional `related` (another place that matters), and optional `fixes` (a label and a text edit, marked safe or risky).

## Code ranges
| Range | Meaning |
| --- | --- |
| `AMP1xxx` | syntax: the text is not shaped right (`AMP10xx` project, features and lint lines; `AMP11xx` blocks) |
| `AMP2xxx` | meaning: the text is shaped right but says something impossible |
| `AMP5xxx` | environment and runtime: depends on what is installed or what happened (`AMP50xx` static warnings, `AMP51xx` regeneration notices) |
| `Lxxx` | lint rules: compile but probably not what you meant |
| plugin prefixes (`ACE1xxx`) | codes contributed by a plugin, declared in its manifest, same record shape |

**Stability:** a code is never reused. A retired code stays in the catalogue (`retired: true`). Each code's `stage` is `static` (found by reading the song) or `runtime` (found by running or regenerating). Messages may be reworded; codes, severity and ranges are the contract.

## Severity and blocking
`error` blocks Apply and leaving the source editor. `warning` and `info` never block. A lint rule raised to `error` blocks like any error.

## Ranges
A diagnostic points at the **smallest range that is wrong** (the parameter name, not the whole line). Where a code can point at two places the range is the one the message talks about; the other is `related`.

## Lint rules
Nine rules in spec 0.1 (see `lint-rules.json` for names, defaults and descriptions): L001 unused-output, L002 missing-input, L003 lock-and-refresh, L004 overlapping-parts, L005 dynamic-not-fixed, L006 pinned-outdated, L007 no-path-to-master, L008 name-case-clash, L009 custom-seed.
- **Level per rule** in the song: `lint { L004 error  L005 info }` (only entries that differ from the default). Unknown rule `AMP1007`, bad level `AMP1008`.
- **Suppression:** a comment `# ample-ignore L004, L005` silences those rules for the **next block** only. Since 0.5 it silences **any warning or note** (not only lint rules) by code, in two places: on the line above a block header (which cannot end with a comment), and at the **end of a line inside a block**, for that line only: `program programFor(phase())  # ample-ignore AMP2073`. An **error is never silenced**. The source editor writes these comments for you (the tooltip button **Suppress here**); **Ignore this session** only hides the message in the browser until the page is reloaded and changes nothing in the song.
- **Static checks of helper bodies (0.5):** a helper is read in **every** branch, also in the ones that never run: `AMP2082` a name that is not a parameter, a variable, a constant or a word of the language (an error inside a calculation, where even a pitch class like `b` is never a number); `AMP2044` a literal `return` that cannot be what the helper says it returns; `AMP2083` a path that ends without a value (an `if` without an `else`); `AMP2084` a variable never read; `AMP2085` a statement that does nothing; `AMP2086` a helper no clip, property or helper calls; `AMP2087` a call of a helper whose declaration has a syntax error (instead of a second, confusing `unknown operator`). `AMP2084`, `AMP2085`, `AMP2086`, `AMP2087` are notes; all but errors can be silenced with `# ample-ignore CODE`.
- Lint runs only on blocks that parsed; it never changes the sound.

## Notices about regeneration
`AMP5101` (info): regeneration is only guaranteed to give the same result on this machine with the same plugin, model and GPU setup. `AMP5102` (warning): the toolprint differs from the one that made the take, or the plugin's determinism is unverified. Both are `runtime` stage; the exact wording is in `diagnostics.json` and is shown by the UI, the CLI and the help (regeneration safety).

## Output formats
Text (for people), JSON (the record above, one per diagnostic) and SARIF (later). The corpus compares JSON records as a set sorted by (line, col, code).
