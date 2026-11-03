# The AMPLE language specification

**Spec version 1.0, status: provisional.** This is the description of the AMPLE source language (`.amplet`) and of the data files around it. The machine-readable half lives in `spec/`; when a page here and the data disagree, the data is the one the tools follow and the page is fixed.

**Which syntax?** A song is a list of blocks. A block has a type, a name, a place on the timeline and a body: `drums beats at 1 for 16 {`. Lines inside say what the block does (`pattern ...`) and what feeds it (`from keys.audio -> audio`). The first line is the composition line: `composition "name" { ample 1  tempo 100  master-seed 7 }`.

## Reading order
| Page | What it defines |
| --- | --- |
| [01 Overview](01-overview.md) | Goals, non-goals, the three views, file kinds |
| [02 Lexical](02-lexical.md) | Encoding, lines, comments, names, strings, numbers |
| [03 Syntax](03-syntax.md) | The grammar (EBNF), statements, error recovery |
| [04 Types and ports](04-types-and-ports.md) | Port types and when two ports connect |
| [05 Blocks and templates](05-blocks-and-templates.md) | Block classes, parameters, the template catalogue |
| [06 Seeds and determinism](06-seeds-and-determinism.md) | Master seed, derived seeds, locks, freeze, bake |
| [07 References and modules](07-references-and-modules.md) | Names, from lines, plugins, files, resolution |
| [08 Diagnostics](08-diagnostics.md) | Every code, severity, lint rules, suppression |
| [09 Formatting](09-formatting.md) | The canonical text form |
| [10 Lyrics format](10-lyrics-format.md) | Timed lyrics files |
| [11 Versioning and compatibility](11-versioning-and-compat.md) | Spec versions and stability |
| [12 Note pipelines](12-note-pipelines.md) | `notes a |> bars(1..=4) |> transpose(+12)`, ranges, units, the bake stage |
| [13 Randomness](13-randomness.md) | Seeds, `stable`, random values, random streams |
| [14 Helpers, types and dynamic code](14-helpers-types-and-dynamic.md) | Helpers, effects, capabilities, `bake EXPR as V` |
| [15 Musical types](15-musical-types.md) | Pitch, velocity, lengths and the types of the operators |
| [16 Capabilities and policy](16-capabilities-and-policy.md) | What a song may do, and how an assistant can only narrow it |
| [17 Notes as formulas](17-note-completeness-and-formulas.md) | Every setting of a note, `set(...)`, formula rows, the pattern finder, proving "the same" |
| [18 Comments and doc comments](18-comments-and-doc-comments.md) | `#` and `/* */` comments, `/** */` and `##` doc comments with `@param {Type|Type}`, and the hover of every helper and built-in |
| [19 Components](19-components.md) | Reusable typed blocks of Notes code: `component`, ports, properties, attributes, instances, versions, hashes and seals, imports, deprecation and migration |

## The machine-readable half
`spec/` holds the same facts as data: `grammar.ebnf`, `tokens.json`, `types.json`, `classes.json`, `templates/*.json`, `diagnostics.json`, `lint-rules.json`, `plugins.json`, `version.json` and JSON Schemas in `schema/`. A program can read them instead of hard-coding the language.
