# 03 Syntax

The grammar below is the same text as `../../spec/grammar.ebnf` (a test compares them). It describes the **structure**; the meaning of block types, parameters and ports comes from the template data ([05](05-blocks-and-templates.md)).

```ebnf
(* AMPLE source grammar, spec version 1.0 (provisional). One statement per line; blocks span lines.
   Notation: ISO-style EBNF; "..." terminals; [ ] optional; { } repetition; ( | ) choice; (* *) comment.
   The prose in docs/language/03-syntax.md contains the same grammar; validate-spec checks they are equal. *)

file        = { line } ;
line        = blank | comment | composition | features | settings | lint | layout | use | block ;
blank       = eol ;
comment     = "#" , { any-char } , eol ;
ignore      = "#" , "ample-ignore" , rule-code , { [ "," ] , rule-code } , eol ;

composition = ( "composition" | "project" ) , string , "{" , [ "ample" , integer ] , "tempo" , number , [ "meter" , integer , "/" , integer ] , "master-seed" , integer , "}" , eol ;   (* "project" is the old word: still read, with AMP1117 *)
features    = "features" , "{" , { feature-pair } , "}" , eol ;
feature-pair = module , ( "on" | "off" ) | "assist" , assist-level ;
module      = "quality" | "credits" | "signing" | "secrets" | "lyrics" | "git-mirror" ;
assist-level = "off" | "diff" | "button" | "auto" ;
settings    = "settings" , "{" , { setting-pair } , "}" , eol ;
setting-pair = identifier , "." , identifier , whitespace , ( string | setting-atom ) ;
setting-atom = { any-char-except-space-or-brace } ;
lint        = "lint" , "{" , { rule-code , level } , "}" , eol ;
level       = "off" | "info" | "warning" | "error" ;
rule-code   = "L" , digit , digit , digit ;
layout      = "layout" , name , integer , integer , eol ;

use         = "use" , module-ref , "as" , name , "{" , eol , { use-line } , "}" , eol ;
module-ref  = ( identifier , { "/" , identifier } | string ) , [ "@" , version ] ;
use-line    = blank | comment | parameter | from ;

block       = block-header , "{" , eol , { block-line } , "}" , eol ;
block-header = block-type , name , [ pinned-version ] , [ placement ] , { flag } , [ freeze ] ;
block-type  = identifier ;
pinned-version = "pinned" , "@" , version ;
version     = digit-run , { "." , digit-run } ;
placement   = "at" , part , { "," , part } ;
part        = integer , "for" , ( integer | "auto" ) , [ "as" , name ] ;
flag        = "bake" | "lock" | "refresh" | "disabled" ;
freeze      = "freeze" , ( "seeds" | "plan" ) ;
block-line  = blank | comment | parameter | from | expanded-midi-notes | notes-expression | random-stream | embedded-sequence ;
expanded-midi-notes = "notes" , "{" , eol , { comment | midi-note } , "}" , eol ;
notes-expression = "notes" , expression-text , eol , { { blank | comment } , "|>" , expression-text , eol } ;   (* a note pipeline: docs/language/12-note-pipelines.md; a pipe line continues the expression, so do braces and parentheses that are still open *)
expression-text = { any-char-except-eol } ;
random-stream = "random-stream" , whitespace , identifier , whitespace , expression-text , eol ;   (* a named random cursor of a clip: docs/language/13-randomness.md *)
embedded-sequence = "sequence" , "{" , eol , { sequence-line } , "}" , eol ;
sequence-line = { any-char-except-eol } , eol ;
midi-note   = "note" , "{" , eol , { comment | midi-note-field } , "}" , eol ;
midi-note-field = identifier , value-text , eol ;
parameter   = identifier , value-text , eol ;
from        = "from" , name , "." , name , "->" , name , eol ;

name        = identifier | string ;
identifier  = letter-or-digit-or-dash-or-underscore , { letter-or-digit-or-dash-or-underscore } ;
string      = '"' , { string-char | escape } , '"' ;
escape      = "\" , ( '"' | "\" | "n" | "t" ) ;
number      = digit-run , [ "." , digit-run ] ;
integer     = digit-run ;
value-text  = { any-char-except-eol } ;

(* A module file, modules/NAME.module: the declarations first, then ordinary blocks and nested use statements.
   Inside the module, "from in.<input> -> port" reads a module input; "$name" in a value stands for a parameter. *)
module-file = { blank | comment } , module-decl , { blank | comment | param-decl | input-decl | output-decl | block | use } ;
module-decl = "module" , module-ref , "version" , version , eol ;
param-decl  = "param" , identifier , value-text , eol ;
input-decl  = "input" , identifier , eol ;
output-decl = "output" , identifier , name , "." , name , eol ;
```

## Statements
| Statement | Shape | Notes |
| --- | --- | --- |
| composition | `composition "name" { ample 1  tempo 92  master-seed 424242 }` | required exactly once; missing is `AMP1001`. `ample N` is the AMPLE version the song was written for (see below); the old word `project` is still read, with the warning `AMP1117` and a quick fix |
| features | `features { quality off  lyrics on  assist diff }` | modules are on/off, `assist` is `off diff button auto` |
| lint | `lint { L004 error  L005 info }` | only entries that differ from the default; levels `off info warning error` |
| settings | `settings { acestep.synthModel sft  acestep.vocalIsolation ace }` | plugin settings of this song, pairs of `plugin.name` and a value (text with spaces in quotes); part of the plan of the blocks of that plugin, so changing one makes a new take; at most one line; malformed is `AMP1112`. Project-level settings are only the starting point for new songs |
| layout | `layout "name" 280 20` | node editor position; no meaning for the sound |
| block | `type name [pinned@version] [at ...] [flags] [freeze ...] {` ... `}` | see below |
| comment | `# text` | `# ample-ignore L001` silences rules for the next block |

Finals are project metadata kept in `.ample/finals.json` and managed through the Finals view or API; they are not a statement or block in the AMPLE song language.

## Blocks
```
drums beats at 1 for 8 as intro, 9 for 8 as outro lock {
  kit kit/tape
  from chords.notes -> trigger
}
```
- **Header order is fixed:** type, name, `pinned@version` (only for a pinned block), placement, flags, freeze. A block that is not pinned follows the definition of its type; whether it is dynamic (`live`) comes from that definition too.
- **Pinned:** `piano keys pinned@2.1 at 1 for 8 {` keeps that version of the block: the same major (the same sound), at least that minor. Block versions are `major.minor` only.
- **Placement:** `at <bar> for <bars> [as <name>]`, several separated by commas. Parts of one block may not overlap (lint L004).
- **Flags:** `bake` (the take is stored and used), `lock` (the value is kept), `refresh` (always fetch again), `disabled` (the block produces no take). A connected block that requires a disabled block is implicitly disabled with the warning AMP5253; an unconnected disabled block is simply omitted. `lock` with `refresh` is contradictory (L003).
- **Freeze:** `freeze seeds` or `freeze plan` ([06](06-seeds-and-determinism.md)).
- **Body lines:** a **parameter** is `name value` (the value is the rest of the line, free text, validated by the template); a **from** line is `from <block>.<output> -> <input>`. A parameter name appears once per block.
- The closing `}` is alone on its line.

### Expanded MIDI notes

Only `midi-clip` may use a nested `notes` body. It represents the same notes as the compact `notes` parameter and remains in source after editing:

```ample
midi-clip verse {
  ppq 480
  notes {
    # comment stays with this note
    note {
      bar 18
      start 22
      length 72
      pitch 121
      velocity 96
      channel 5
      bank 0
      program 60
      tie-id 2
    }
  }
}
```

`bar` is one-based. `start` and `length` use quarter-note beats (up to six decimal places) when `ppq` is absent or uses the legacy million-unit scale; otherwise they use integer ticks at the clip's `ppq`. `pitch` is 0–127 and `velocity` is 1–127. `channel`, `bank` and `program` are optional as a complete set; `tie-id` is optional and identifies bar-split segments. The clip remains in 4/4 for note placement; imported meter events stay available in the MIDI event source. `#` comments are retained verbatim and ignored when building note data.

## Error recovery (normative)
A conforming parser **never stops at the first error** and never throws on any input. Required behaviour:
1. A line that is not any statement reports `AMP1100` and parsing continues with the next line.
2. A bad block header reports an error (`AMP1102`-`AMP1106`); the block body is still consumed up to its `}` so later blocks are found. If the header yielded a type and name, the block is kept in the tree (so the editor can still show it); otherwise its body is skipped.
3. A bad body line (`AMP1107`-`AMP1109`) does not remove the block; the rest of the body is parsed.
4. An unclosed block at the end of the file reports `AMP1101` at the block header and keeps the block.
5. Semantic checks (`AMP2xxx`, lint) run on everything that parsed, even when syntax errors exist; blocks that are known to be broken (skipped) take no part in them.
6. The order of diagnostics in the output is by position (line, column, then code) when a tool prints them; implementations may produce them in another order internally, and the corpus compares them **as a set sorted by (line, column, code)**.

The recovery corpus (`../../spec-tests/recovery/`) fixes the expected blocks and codes for broken inputs.

## Modules (use and module files)
`use <module>[@version] as <name> { ... }` puts an instance of a reusable module into the song. The body gives the module's parameters (`name value`) and connects its inputs (`from block.output -> input`, where the last word is the **module's** input name). Other blocks read the instance like a block: `from intro.audio -> mix`, where `audio` is one of the module's outputs. The parser only checks the shape; the module is found and expanded when the song is built (`ample modules expand`, [07](07-references-and-modules.md)).

## AMPLE versions
`ample N` says which version of the language and of the built-in blocks a file was written for. It is a whole number that goes up only when a change could alter how an existing song sounds or reads; it is not the number of the program.

- **In the composition line:** `composition "name" { ample 1  tempo 92  master-seed 424242 }`. A song that says nothing is read as the current version and gets the info `AMP1118` with a quick fix that adds it. A number this AMPLE does not know (`0`, or higher than it reads) is the error `AMP1119`.
- **In a definition, not in a song:** the definition of a built-in block (and of a component) says since which AMPLE version it reads the same (`"stableSince"` in the template, 1 when it says nothing). A song cannot claim that for its own blocks. When a block was changed after the version a song was written for, that block is shown read-only (with its migrated values) until the song is migrated.
- **Never upgraded by itself:** an older song is carried to the current version in memory, step by step (`AmpleVersions.Upgrade`), and the text on disk only changes when its author migrates it. Every step must keep the sound: where a default or a name changed, the step writes the old value explicitly.
- **Files that import another file** keep that file's own number: a component imported with `use` is read as the version its own file states, never as the importing song's.

## Controls
A **control** is a number over time that comes out of a block (`follow`, `curve`, `map`) and steers a parameter that can be automated (`volume` gain, `pan`, filter `cutoff`, the `mix` of the effects, compressor `threshold`, stretch `speed`, pitch `semitones`; the templates mark them with `"automatable": true`).

- **Types.** `control` is the general one: a level from 0 to 1 that is scaled to the range of the parameter. `control<Hz>`, `control<dB>`, `control<%>`, `control<ms>`, `control<st>`, ... know their unit and only steer a parameter in that unit (`AMP2253`). A control never connects to audio or MIDI.
- **Expression.** A parameter may read controls: `cutoff 600 Hz + 3000 * traffic.level`. Numbers (with a unit), `+ - *`, brackets and `block.output`. Everything is integer fixed point with three decimals, so a build is the same everywhere; a general control is a fraction (so `3000 * level` is 0 to 3000). Errors: `AMP2251` (not valid), `AMP2252` (not a control).
- **Connection.** `from traffic.level -> cutoff` connects a control to the parameter named like the input. A parameter has one source: an expression and a connection together are `AMP2254`.
- **Build.** The block that reads a control waits for the block that makes it (cycles are `AMP2010`). The control is a take of its own (`CTL1`: 1000 integer values per second from the first bar of its source); before the renderer runs, a parameter that reads a control is turned into the points of an automation curve, so every renderer follows it like `auto(...)`.
- **Components.** A component port may be `Control` or `Control<unit>`, `follow` and `map` are stages of an audio pipeline (`emit audio |> follow(band="low") |> map(unit="Hz", out_high=4000) -> traffic`), `curve` starts a control from nothing (`emit curve(value="auto(1:0, 4:1000)") -> sweep`) and `fire` turns a control into notes that an instrument plays (`emit audio |> follow(band="low") |> fire(note=36) |> piano() -> audio`).

## repeat in the process of an audio component
`audio |> repeat(times=cars, delay=2bar, length=24bar)` makes the sound again and again and mixes the copies into one sound. `times` is a whole number from 1 to 16 and may be a property (`property cars: Int = 4`), so the component decides how many copies there are; `delay` is the time before the first copy (default none); the copies are spread evenly over what is left of `length` (the first at `delay`, then every `(length - delay) / times`), or at a fixed distance with `every=4bar` instead of `length`. Times are written with a unit (`bar`, `beat`, `s`, `ms`) and may be calculated from properties. Example: `demo-projects/components/components/street-cars.amplec` (cars that pass a street, with the road also as an output of its own to tweak).

## trim
`trim` finds where the sound of a recording is (the stretches above `threshold` that a silence of at least `gap` separates) and cuts the silence around it, keeping `padding`, with a short `fadein` and `fadeout` if you like. Several stretches in one recording: `sections warn` (default) cuts from the start of the first to the end of the last and puts a warning in the report of the block; `all` does the same without the warning; `first` and `last` take that one stretch. In a component: `road |> trim(sections=all) |> repeat(times=cars, length=length)`, so that each car starts at its first sound and a replaced recording still fits.

## if / else and silence in a component

In the process of an audio component a branch can be chosen by a property: `if (tone) { ... } else if (level) { ... } else { ... }`. The condition is a Bool property, a number (0 is false) or a calculation, and the branch that is taken is known when the graph is built, so the lines of the other branches are not part of it. A `let` in a branch is available after it, so every branch should give the same names. In a calculation a Bool is 1 or 0, and `if(condition, a, b)` picks one of two values.

`silence()` is blank audio as long as the block it is in (`auto`); `silence(10s)` is as long as you say. It stands where a branch has no sound: `let bed = if...` is written as `if (tone) { let bed = sad |> gain(-6dB) } else { let bed = silence() }`.

## Meter

`composition "waltz" { ample 1  tempo 96  meter 3/4  master-seed 7 }`: the optional `meter` sits between `tempo` and `master-seed`. Without it a song is in 4/4. The top number is the beats of a bar (1 to 16), the bottom number the note that gets a beat (2, 4, 8 or 16); anything else is AMP1120 and the song stays in 4/4.

What follows from it, for the whole song: a bar lasts `4 x top / bottom` quarter-note beats (3/4 and 6/8: three, 5/4: five, 7/8: three and a half); `at 3 for 2` still counts bars; the beat of a note (`bar:beat:length:pitch:velocity`) runs from 0 up to the beats of the bar; `1bar` in the note language is one such bar; the grid of a `drums` pattern and of a sequencer row is sixteenth notes, so a bar has 12 steps in 3/4 (a pattern of 16 characters is cut after 12 in a 3/4 song, write 12); `chords Am.F` shares a bar between two chords; a note value such as `1/16` stays a fraction of a whole note (four beats) in every meter; the tempo is quarter notes per minute in every meter, so 6/8 at 120 is a bar of three quarter notes (1.5 seconds).

The studio follows the meter: the piano roll (the bar lines, the snap to a bar, the metronome accent), the step grid of the drum and strum editors (12 steps in 3/4), the lengths shown in seconds, and the local preview in the browser all use the beats of the bar. One meter for the whole song: a change of meter inside a song, and a meter per clip, are not built; MIDI import and export use the meter of the file: the module is cut at the bar lines of its first time signature, the converter records it as `meter=N/D` and warns that the song using the module must say the same meter, and the export reads it back so the notes return on the same ticks. A file that changes meter inside gets one meter and a warning.
