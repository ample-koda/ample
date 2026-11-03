# 01 Overview

## Goals
- A song is **text** that can be read, diffed, versioned and generated. The node editor, the arrangement and the source editor are three views of the same text (source-and-views).
- **Reproducible by default:** every random choice is derived from a master seed; AI results are recorded as takes with their plans ([06](06-seeds-and-determinism.md)).
- **Good errors:** one front end produces diagnostics with stable codes and exact ranges for the editor, the terminal and CI ([08](08-diagnostics.md)).
- Small and checkable: a line-oriented syntax a person can write by hand and a tool can recover from.

## Non-goals (spec 0.1)
General-purpose programming (no variables, loops or functions yet); defining new block types in the language (templates come from the library and plugins); audio DSP details (the engine's business).

## The model in one paragraph
A song file declares a **composition** (name, language version, tempo, master seed), optional **features** and **lint** settings, and a list of **blocks**. A block has a **type** (from the template catalogue), a unique **name**, an optional **`pinned@version`**, optional **placements** on the timeline, optional **flags**, **parameters** and **`from`** lines that connect another block's output port to one of its own input ports. The blocks and their `from` lines form a directed acyclic graph that the engine builds into audio.

## File kinds
| File | Format | Defined in |
| --- | --- | --- |
| `*.amplet` | the song source | pages 02 to 09 |
| `*.amplelrc` (lyrics) | timed lyrics | [10](10-lyrics-format.md) |
| `pack.json` | sound pack manifest | |
| `plugin.json` | plugin manifest | |
| `ample.lock` | pinned versions and hashes, written by the tool | |

## Conformance
A tool conforms to spec 0.1 when it passes the corpus in `spec-tests/`: the same diagnostics (code, severity, range) for every case in `invalid/`, no errors for every case in `valid/`, identical output for `format/`, `roundtrip/` and the recovery expectations in `recovery/`.
