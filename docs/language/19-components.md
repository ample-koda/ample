# 19 - Components

Status: **being built**. A component is a reusable, typed block made of Notes code. It looks like a native block in the editor and the palette, and it stays transparent: its code is readable, its contract is typed, its versions are explicit, and a song always knows exactly which version of which code it used.

Two requirements shape the design:

- **R-A. A song never changes without its owner knowing because the code of a component changed.** The semantic hash of every component a song uses is pinned in `ample.lock`; a different hash is AMP2203 until the change is accepted.
- **R-B. A build never starts over when nothing that matters changed.** An instance becomes the notes it makes; the plan identity of everything downstream follows the resulting notes. A new description, a display name or a KODA fact never changes the semantic hash, so nothing is built again.

## 1. The declaration

```
/** Repeats the notes, one copy per step. */
#[Category("Notes")]
component alex.notes.tripleNotes version 1.0.0 {
  id "com.alex.notes.triple-notes"      # optional; the default is local:<name>; needed to seal and export
  current                                 # optional: this version is the default of its family

  input source: Notes {
    optional true
    fallback localNotes                   # the property that stands in when nothing is connected
  }

  property localNotes: Notes {
    editor jam                            # Jam edits it
  }

  #[MinValue(1), MaxValue(16), Step(1)]
  property copies: Int = 3

  property spacing: Duration = 1bar

  output notes: Notes

  process {
    let n = connected(source) ? source : localNotes
    for i in 0..copies {
      emit n |> shift(i * spacing)
    }
  }
}
```

- **Name.** A path of one or more parts (`alex.notes.tripleNotes`). A name with one part may not be the name of a native block type (AMP2209); a namespace avoids it. The name is how source writes the component; the **canonical id** says what it is. Names can be remapped and reexported, the id never changes (the same component under two names is one component).
- **Header.** `component <name> [version <x.y.z>] [current] [id "..."]`, then `{`. The header may run over lines (`version "1.0.0"` on its own line is fine). `version` may also be written inside the body.
- **Ports.** `input <name>: <Type>` and `output <name>: <Type>`, optionally with a body (`optional [true]`, `fallback <property>`). In this version ports are Notes, and there is one output (AMP2219 otherwise); the model is `Port<T>`, so other types come later without a redesign.
- **Properties.** `property <name>: <Type> [= <default>] [{ editor jam }]`. The types are the musical types of page 15 (and List, Range of them): there are no types of their own. A default is required except for Notes. A default must be a value of its type and inside its range (AMP2204).
- **Process.** `process { ... }` is the body of a helper (page 14): the inputs and properties are its parameters in the order they are declared; `emit`, a `return` or the last expression give the output. `connected(source)` (or `source.connected`) says whether an optional input has a connection. All limits, capabilities and the pure or dynamic effect of helpers apply; a process that uses the clock, OS randomness or the environment needs `requires clock` (and the policy must allow it).
- **Inside a song or a file.** A component is declared at the start of a line, in a song (`.amplet`) or in a `.amplec` file (a file of declarations: components, helpers, constants, types, `import`, `reexport`). Its lines are not song lines.
- **Doc comments** (page 18) work above the component, the ports and the properties; `@param` per property, the hover shows the contract.

## 2. Attributes

`#[Name(args), Name2]` on its own line directly above a component, a port or a property. Only the built-in attributes exist (AMP2205 for anything else); each says where it may stand and on which types (AMP2206) and how many arguments of which kind it takes (AMP2207). To every tool that does not know attributes the line is a comment.

| Attribute | On | Meaning |
| --- | --- | --- |
| `MinValue(n)`, `MaxValue(n)`, `Step(n)` | a property of an ordered type (Int, Float, Percent, Duration, Interval, Gain, ...) | the range and the step of the editor; instances outside the range are AMP2218 |
| `Options("a", "b")` | a String property | the allowed values, shown as a choice |
| `Editor(jam)` | a Notes property | Jam edits it (also `editor jam` in the body) |
| `DisplayName("..")`, `Description("..")` | anything | presentation |
| `Category("..")` | the component | the group of the palette |
| `Tags("a", "b")` | the component | words the Library search also finds (`#a`, `#b`); lower case, never part of the identity or the semantic hash |
| `Experimental` | the component | may still change in ways its version does not say |
| `KodaFact("..")` or `KodaFact(text="..", appliesTo="copies")` | anything | a fact for the assistant: advisory, it never changes the build |

Prefer a type to an attribute when the type already says it (`Percent` instead of `#[Unit("%")]`).

## 3. Instances

An instance is a block whose type is the component:

```
alex.notes.tripleNotes introTriple stable at 2 for 3 {
  copies 3
  spacing 1bar
  from intro.notes -> source
}
```

- The header is the usual `type name [pinned@version] [at ...] {`; `pinned@<version>` is a **request** for a version of the component (`pinned@1.8` the 1.8 line, `pinned@1.8.0` exactly, `pinned@^1.2` compatible, `pinned@~1.2` patch-level, `pinned@4.0.0-beta.1` a pre-release by name); without it the instance follows the current version.
- **Default version:** an explicit request, else the version marked `current`, else the highest release; a pre-release is never chosen unless it is the only one or is asked for by name. At most one `current` per component (AMP2214). Several versions of one component can coexist.
- The property lines are checked against the contract (type, range: AMP2218); `from` lines connect the inputs; consumers read the output as `from introTriple.notes -> x` (the output name is whatever the component calls it).
- **What an instance makes.** Before a song is built each instance becomes a `midi-clip` with the same name whose notes are the call of the process of its component. So the note flow, bakes, Jam, previews, the build and the plan identity work on an instance without any change, and **three instances share one definition** (one helper, three calls).
- A component that comes from an imported file is added to the expanded text with its whole file (its helpers and constants come with it), once.

## 4. Hashes, pins and seals

| | Covers | Used for |
| --- | --- | --- |
| **semantic hash** | canonical id, version, ports, property types and defaults, the constraint attributes, the process (comments out, whitespace collapsed), the hashes of every helper and constant it uses, `requires`, the migrations | pinning in `ample.lock` (R-A), deciding that a rebuild is needed (R-B) |
| **payload hash** | everything above and the documentation, the presentation attributes, `ui`, `deprecated`, KODA facts | what a seal and a signature protect |

- **Pins (R-A).** The lock file has a `component` section: `<canonical id>@<version>` with the semantic hash. The first use pins; a different hash later is **AMP2203** with the fix "accept the change" (it re-pins). An unsealed component that changed shows an info in the build log, once per build.
- **Seal.** `seal { payload sha256:..  semantic sha256:..  [signed-by ".." key .. signature ..] }` is written by the engine, never by hand. A sealed version is immutable: if the component changes afterwards the seal no longer matches (AMP2210) and the component counts as unsealed. The way forward is a new version.
- **Unsealed** is not an error (AMP2211 is an info): it builds, it just never counts as trusted.
- Same id + same version + same payload in two places is one component (deduplicated); same id + same version + another payload is AMP2212.
- A signature is the publisher's attestation over the payload hash (ECDSA P-256, the keys of the user).

## 5. Namespaces, imports, reexports

Namespaces are logical paths, independent of files. One file can hold components in several namespaces; several files can add to one namespace. `import "file.amplec" [as alias]` makes the components of a file available (the alias replaces the first segment of their names: `fx1.notes.tripleNotes`; the Library tab and `ample_component_library` propose one when a name is taken) (found like modules: project, linked folders, packs, the user library); `reexport <path> as <path>` offers a symbol (or a namespace with `.*`) of an import again, keeping its canonical id, version and provenance. Cycles are errors (AMP2217). The same exported path for two different canonical ids is AMP2213 and needs an explicit alias; **the last import never wins**.

### Built-in components

Some components ship with AMPLE (the files in `content/components`, embedded in the product): `ample.voice.clean` (low-cut, denoise, gate and de-esser behind three knobs) and `ample.voice.trim` (skip the first seconds, stop at a second, fade the ends). Every song sees them **by name, without an import line**: when the text of a song names one, the registry adds its file as an implicit import. They are in the Library tab of every project under "Built in" (the plus button adds a block and writes no import). Their ids start with `ample.builtin.`, their versions are fixed (a better sound is a new version, so a song never changes by itself, requirement R-A) and they raise no AMP2211 note. A project file with the same name still wins the usual conflict rules (AMP2212 to AMP2214).

## 6. Deprecation and migration

```
component alex.fx.canonMaker version 3.2.0 {
  deprecated {
    message "Use 4.x; the routing changed."
    replacement alex.fx.canonMaker@^4
  }

  migrate 3.2.0 -> 4.0.0 {
    impact musicalChange                 # none | equivalent | musicalChange | destructive
    message "The result may sound wider."
    rename interval -> transposeBy       # rename a property (copy a -> b keeps both)
    set spread = old.spread * 2          # a typed expression over the old values
    drop legacyStereo                    # remove a property
    port source -> input                 # rename an input (its from lines follow)
  }
  ...
}
```

A deprecated version warns (AMP2220) and never stops a build; a migration is a small, pure, bounded, typed program over the state of an instance (its properties, connections and local notes). An impactful migration (`musicalChange`, `destructive`) is never applied without an explicit acknowledgement; the preview shows the changed properties, ports, the resulting notes, span, event count and semantic hash. Chains (1 -> 2 -> 3) are followed and their impacts combined; a replacement can be another component.

Expressions in `set` are bounded (400 characters, 32 levels) and typed: numbers with an optional unit (`2bars`), `old.<property>` for the value before the step, `+ - * /`, brackets and `min`, `max`, `clamp`, `round`, `abs`. Units must agree for `+` and `-`. A property of the old version that the new one does not have, and that no `drop` removed, is removed anyway and makes the migration `destructive`. A migration without an `impact` line counts as `musicalChange`. A chain (1 -> 2 -> 3) is found by a search over the migrations; `migrate com.other@1.0.0 -> 1.0.0` replaces another component. `ample components migrate <song> <block> <target> [--apply --acknowledge]` and `/api/components/migrate-preview` / `/migrate` show and make it.

## 7. Embedding and extraction

A song can carry its components: `embedded module <name> { ... }` holds complete component declarations with their seals and keeps the module boundary, so an embedded component is the same object as an imported one with the same id, version and payload hash. Actions: embed the dependencies of the selected instances, embed a whole module, use the template source (only when id, version and payload match exactly: a semantic no-op), extract a component or module to a `.amplec`, detach a sealed embedded component for editing (it becomes unsealed).

## 8. Diagnostics

AMP2200 to AMP2228 (see the catalogue): 2200 syntax, 2201 names, 2202 types, 2203 changed since pinned, 2204 defaults, 2205 to 2207 attributes, 2208 output and process, 2209 native name, 2210 seal void, 2211 unsealed, 2212 to 2214 conflicts, 2215 version, 2216 to 2217 import and reexport, 2218 instance, 2219 not supported yet, 2220 deprecated, 2221 migration syntax, 2222 migration available, 2223 version syntax, 2224 id, 2225 attributes without a target, 2226 fallback, 2227 capabilities, 2228 ui.

**Friendly name.** `#[DisplayName("Triple notes")]` above the declaration is the friendly name: the card, the palette and the panel show it; the full name (`alex.notes.tripleNotes`), the canonical id and the version are on hover and in the properties of the block, both visible.

## 9. Audio components

A component with an Audio port is an **audio component**: the same declaration, imports, versions, seal, embedding, migration and palette, and a `process` that is a graph of audio processors instead of Notes code.

```
component alex.fx.wetDry version 1.0.0 {
  input audio: Audio
  property echo: Duration = 0.5beat
  property feedback: Percent = 35%
  property dryLevel: Gain = -3dB
  output audio: Audio
  process {
    let dry = audio
    let wet = audio |> delay(time=echo, feedback=feedback) |> reverb(size=50%)
    emit mix(dry |> gain(dryLevel), wet |> gain(-8dB))
  }
}
```

- **`Audio` is a lazy graph**, never a buffer: `audio |> delay(...)` describes a node, the build renders the blocks it becomes. A component boundary is an abstraction boundary, not a file boundary. Rendering granularity stays the block (each processor has its own take in the cache).
- **The processors are the effect blocks** (one definition, two surfaces): every template with an audio `in` and `out` (delay, reverb, chorus, compressor, eq, filter, limiter, phaser, tremolo, pan, ...). `gain(x)` is the `volume` block, `mix(a, b, ...)` is a `bus`. A second audio input is given by name: `compressor(threshold=-12dB, key=sidechain)`.
- **Arguments are named and typed by the unit of the block parameter**: a time for `ms` (250ms, 0.25beat, 1bar; beats become ms with the song tempo), a Percent for `%`, a gain for `dB`, a frequency for `Hz`. Arithmetic over the properties is allowed (`space * 0.5`). A Bool property can be given to `enabled` (bypass). A Notes operation on Audio (AMP2234), a wrong unit (AMP2232), an unknown parameter (AMP2235) and an unknown name (AMP2231) are errors at the line.
- **A name is defined before it is used**, so a graph cannot loop; feedback exists only through the feedback parameter of a processor. At most 64 processors and 8 outputs (AMP2233). Several outputs: `emit audio |> ... -> name`.
- **Notes in, sound out** (a component with an instrument inside): a Notes input is a value of its own kind and an *instrument* turns it into audio: `emit notes |> piano() |> delay(time=250ms) |> reverb(size=room)`. The instruments are the blocks that read `notes` and give `audio`: piano, bass, guitar, synth, gm, sampler and delay-llama (drums read a trigger and are not offered). Their parameters are named arguments like those of an effect (`piano(...)`, `delay-llama(...)`). The instance is wired like any block (`from melody.notes -> notes`), takes the **placement** of the instance (`at 1 for 2`) onto the instrument block it becomes, or, when it has none, the placement of the clip it reads, and the next block reads its sound (`from voice.audio -> in`). Notes are not audio: notes straight to an output, an effect on notes, an instrument on audio and notes in a `mix` are AMP2232. A Notes *output* next to Audio ports is refused (AMP2230): the process makes audio. An unconnected Notes input says so (AMP2218) unless it is `optional`.
- **An instance is wired like any block** (`from vocals.audio -> audio`; the next block reads `from vocalFx.audio -> in`) and becomes ordinary blocks named `<instance>__<processor><n>` (`delay1`, `reverb1`): provenance for diagnostics, logs and the takes. Node ids are the processor name and its number in the source.
- **Hashes**: the semantic hash contains the process, the contract and `processor=<block>@<version>` for every processor used, so a new version of an effect block is a changed hash; one function (`ComponentHashing.DependenciesOf`) serves seal, check, catalog and build. Documentation and presentation are not in it.
- **Latency and preview are derived**, never claimed: the longest path of processor latencies (a template that really delays its signal declares `latencyMs`; no built-in does, the limiter looks ahead over a whole file without delaying it) and the browser preview (every processor must have one). `ample components graph <song> <block>` and `GET /api/components/graph` show the graph; the board menu has "Show internal graph".
- **Migration**: `port input.audio -> input.signal` renames an input (the `from` lines of the instance follow) and `port output.a -> output.b` an output (the consumers follow); an audible change is declared `musicalChange` and must be acknowledged.
- **Nested components**: an audio process can call another audio component as a processor, with its properties as named arguments: `audio |> alex.fx.dreamEcho(feedback=20%)`. A second audio input is given by name (`key=other`). The called component is built with those values and spliced into the graph (nodes `dreamEcho1_delay1`, ...), so the engine still sees ordinary blocks. Its semantic hash is part of the hash of the caller (`component=name@version:hash`): a change in the nested code is a change in the caller and breaks its seal. At most four deep; a component never calls itself.
- **Notes components call Notes components too** (2026-10-29): in the process of a Notes component, another Notes component is called like a function: `emit alex.notes.echoNotes(alex.notes.octaveLayers(source, layers=2), plays=3, interval=2)`. The first unnamed value is the first input; every other input and every property is given by name (`plays=3`), and what is left out takes the default of the called component (an input without a default and not optional must be given, AMP2248; a name the called component does not have is AMP2248 too). The call is rewritten to the call of the helper that the called process is, so it is evaluated by the same helper system, and the hash of the caller contains the hash of what it calls (change the called component and the seal of the caller breaks). A component file can import the files of the components it calls (`import "components/echo-notes.amplec"` at the top of the file); the song does not see those, they travel along. Demo: `demo-projects/components/12-nested-notes.amplet`. The call can also be a pipe step: `source |> alex.notes.octaveLayers(layers=2) |> alex.notes.echoNotes(plays=3)` (what is before the pipe is the first input). In a **mixed component** a Notes call can stand before the instrument: `emit notes |> alex.notes.octaveLayers(layers=2) |> piano()` makes a `midi-clip` block (named after the instance and the call) whose notes are the call, and the instrument reads that clip; such a component has no live preview (the call is not evaluated in the browser). A file that imports with an alias (`import "components/up.amplec" as p`) calls the component by the alias; the registry writes the declared name into what the expansion adds, so two files that use the same alias for different components do not clash.
- **Pin the version of what you call** (2026-10-29): a call takes the default version of the called component (the one marked `current`). Write the version after the name to keep the one you tested: `alex.notes.up@1.2.0(source)`, `@1.2` or `@^1.2` (the same requests as an instance `alex.notes.up@1.2 name { ... }`), in Notes calls and in audio calls (`audio |> alex.fx.dreamEcho@1.0.0(feedback=20%)`). A request that no version matches is AMP2248. A component that is not sealed and calls a family with more than one version without a pin gets an info, AMP2249 (a sealed one breaks its seal when the default changes, because the hash contains what it calls).
- **Where a call is read** (2026-10-29): only in code, not in the strings and comments of a process (a bracket that never closes in a comment hides nothing); a dotted name that looks like a component and is none in sight is AMP2248 ("is its file imported?"), not a syntax error. A file of components opened on its own (the editor, `ample check file.amplec`) reads its own imports, so a call of an imported component checks without a false error. A Notes component can have `String` and `Bool` properties; `chords("Am F", 1bar, 3, 70)` takes length, octave and velocity in that order without names.
- **Circles** (2026-10-29): a component never calls itself, directly or through other components, in both kinds. When A calls B and B calls A the check says so and names the circle (`alex.notes.ping -> alex.notes.pong -> alex.notes.ping`): AMP2247, on every component of the circle (for audio components at the call that closes it). The calls inside a circle are left out of the helper, so a circle is reported, never run. Chains are at most four deep (AMP2233).
- **Latency** of branches that meet is aligned: the shorter branch gets a `shift` block (`align1`, ...). Between the tracks of a song the same alignment is optional: `settings { audio.latencyCompensation on }` puts a `shift` in front of the earlier inputs of a mixer, bus, send or vocoder and a `cut` in front of the master (AMP2250); it is off by default, so a song without it keeps its plan ids. A pass-through (`emit audio`) is allowed.
- Limits of this version: no external plugins, seeds only through processors that have a seed parameter.

## 10. Layout and live preview

A component can design its own controls: knobs, sliders, switches, choices and an xy pad, in rows and groups. The layout is **presentation only**: it is part of the payload hash (a seal protects it) and never of the semantic hash, so changing it never changes what the component makes (R-A) and rebuilds nothing (R-B). The ranges are different: `MinValue`, `MaxValue` and `Step` constrain the value, so they are semantic.

```
#[MinValue(0%), MaxValue(90%)]
property feedback: Percent = 30%
#[MinValue(0%), MaxValue(100%)]
property room: Percent = 40%
property delayTime: Duration = 0.25beat

ui {
  row {
    knob feedback label "Echo" size l
    knob room size l
  }
  field delayTime
  meter audio
  card {                       // the compact face on the board card
    knob feedback size s
  }
  preview live                 // opt in to the live preview
}
```

- **Widgets** (a closed set, no scripts): `knob`, `slider` (a number property, with its range from `MinValue`/`MaxValue`/`Step`, else what the type usually means), `toggle` (Bool), `choice` (Options or Bool), `xy x=a y=b` (two number properties, one write for both), `readout` (a value), `meter` (an audio output: its level while the live preview plays), `jam` (a Notes property), `curve` and `field` (the standard field of the panel; a curve has no editor yet). Options: `size s|m|l`, `label "text"`, `tint <colour>`. A **tint** colours a widget or a group (`group "Echo" tint blue { ... }`): a name (accent, green, yellow, orange, red, pink, purple, blue, teal, gray) or a text such as `"#ff8800"`; `#[Tint(red)]` on a property is the shortcut without a block; anything else is AMP2246. A property can have a **friendly name**: `#[DisplayName("How many")]` is the label in the panel, on the knob and in the rows, and the tooltip shows both (`How many (copies)`); `label` in the layout overrides it. The **curve** widget draws a text property that holds points, `x:y` pairs from 0 to 1 (`"0:0 0.5:1 1:0"`): double click adds a point, right click removes one, a drag moves it, and the value is written in that format (it is a value of the component; the process language does not read curves yet).
- **Containers**: `row`, `column`, `group "Title" { ... }`, and `card { ... }` at the top level. At most 4 deep and 64 items (AMP2243).
- **Without a block** the layout is made from the properties: a plain field each. `#[Widget(knob)]` and `#[Group("Envelope")]` on a property are the shortcut for simple cases; `#[LivePreview]` on the component is the shortcut for `preview live`. Properties that the layout does not show keep their standard rows.
- **Where it shows**: the properties panel (the whole layout), the board card (the `card` block; the card gets taller and a knob on it never moves the block) and a window (the ⤢ button).
- **Dragging is a preview**: while a widget moves nothing is written; on release one `setParameter` is (one undo step). An xy pad writes both properties in one step.
- **Checks**: AMP2240 (not a word of the layout), AMP2241 (no such property or output), AMP2242 (a widget that does not fit the type), AMP2243 (too big or too deep), AMP2244 (the same property twice), AMP2245 (a live preview that cannot be).
- A **Bool or String property** written on an instance is checked as such (true or false, any text); only numbers go through the number evaluator.

**Live preview** (opt-in, advisory): an audio component that says `preview live` gets a **Live** switch. The sound that feeds the block loops in the browser through a real-time chain made of the same live effects the built-in blocks use (eq, filter, compressor, distortion, delay, reverb, chorus, gain; a mix is a sum), and the sound changes while a knob moves. The server stays the only evaluator of the process (units, beats to milliseconds, arithmetic over properties): the browser sends the values of the knobs to `POST /api/components/graph-values` and sets its chain from the answer. Capability is derived, never claimed: a component can run live when every processor has `"livePreview": true` in its template and none has a second audio input (a sidechain key); otherwise the switch is off and says which processor stops it (AMP2245). The preview is approximate (Web Audio is not the engine); it never writes a take, never touches a plan id and never builds: the committed build is the truth. It needs the source built first (it says so instead of building by itself). A component with **one instrument** inside (Notes in, sound out) loops the sound the *server* rendered for that instrument (its take, so the instrument stays deterministic) and runs the effects after it live; with more than one instrument the switch stays off ("more than one instrument"). **The Delay Llama is the exception that sings in the browser itself** (the live llama worklet of Jam): a component with a Delay Llama inside needs no build, the notes are those of the midi-clip that feeds it (looped), and its own settings (vowel, size, unison, vibrato, ...) change the voice while a knob moves.

## 11. Not in this version

A Notes output next to Audio ports, a live preview of Notes components, of the instrument itself while its own parameters move, and of processors without a real-time chain (a limiter, a sidechain key), a meter on an input, native code, a package registry, a trust store, several implementations of one version, user-defined attributes and types.
