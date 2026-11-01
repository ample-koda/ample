---
title: What is a component
order: 10
lead: A component is a block of your own. It looks like any other block, and its code stays readable.
---
Components are still being built. The parts on these pages exist, but details may change before the specification is settled.

## The idea

A component is a reusable block made of note code. It has typed **inputs**, **properties** and an **output**, like any block, and a `process` that says what it does. It stays transparent: you can read its code, its contract is typed, its versions are explicit, and a song always knows which version of which code it used.

Two rules shape it:

- A song never changes without you knowing because the code of a component changed. Every component a song uses is pinned, and a changed one is reported until you accept it.
- A build never starts over when nothing that matters changed. A new description or display name never forces a rebuild.

## Writing one, and using it

{{amplet A component that repeats notes, and an instance of it}}
composition "Component" { ample 1  tempo 100  master-seed 20261017 }

/** Repeats the notes, one copy per step. */
#[Category("Notes")]
component alex.notes.tripleNotes version 1.0.0 {
  input source: Notes {
    optional true
    fallback localNotes
  }

  property localNotes: Notes {
    editor jam
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

midi-clip intro at 1 for 1 {
  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100
  bars 1
}

alex.notes.tripleNotes triple at 1 for 3 {
  copies 3
  spacing 1bar
  from intro.notes -> source
}

synth voice at 1 for 3 {
  wave triangle
  gain -12 dB
  from triple.notes -> notes
}

master out {
  from voice.audio -> audio
}
{{/amplet}}

- The **name** is a path of one or more parts (`alex.notes.tripleNotes`). A namespace keeps it from clashing with the name of a block.
- An **instance** is a block whose type is the component. Before the song is built, each instance becomes a `midi-clip` with the same name, so notes, previews and the build work on it like on any clip. Three instances share one definition.
- `/** ... */` above a component, a port or a property is its documentation.

{{deeper Where a component can live}}
A component is declared at the start of a line, in a song or in a file of declarations (`.amplec`) that also holds helpers and constants. `import "file.amplec"` makes the components of a file available, and `reexport` offers one again under another path without changing what it is. A song can carry its components in an `embedded module`.
{{/deeper}}

{{ample A component is built from helpers and pipelines. | /learn/ample/language/expressions=Expressions, /learn/ample/components/properties-and-ports=Properties and ports}}
