---
title: Expressions
order: 30
lead: A value can be a calculation: a helper you wrote, a chain of steps on notes, a number with a unit.
---
## Numbers with units

Most values carry their unit: `250ms`, `1bar`, `-3dB`, `50%`, `440Hz`. A bar or a beat is a time that follows the tempo and the meter of the song.

## Pipelines on notes

Note data is a value. Instead of typing the notes you can say where they come from and what happens to them. The steps are chained with `|>`, each one takes the notes of the step before it.

{{amplet The same three notes, one octave higher}}
composition "Pipeline" { ample 1  tempo 100  master-seed 20261017 }

midi-clip theme at 1 for 1 {
  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100
  bars 1
}

midi-clip up at 1 for 1 {
  notes theme.notes |> transpose(12)
  bars 1
}

synth voice at 1 for 1 {
  wave triangle
  gain -12 dB
  from up.notes -> notes
}

master out {
  from voice.audio -> audio
}
{{/amplet}}

The steps are the same operations as the blocks of the same name (`transpose`, `quantize`, `humanize`...): one definition, so a block and a step cannot drift apart.

## Helpers

A helper is a small function you write once and call wherever you need it. It has typed parameters, and its value is the last expression.

{{amplet A helper that makes a canon voice}}
composition "Canon helper" { ample 1  tempo 100  master-seed 20261017 }

helper canonVoice(Notes source, Number interval, Duration delay) -> Notes {
  source |> transpose(interval) |> shift(delay)
}

midi-clip theme at 1 for 2 {
  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100
  bars 2
}

midi-clip canon at 1 for 4 {
  notes canonVoice(theme.notes, 7, 1bar)
  bars 4
}

synth voice at 1 for 4 {
  wave triangle
  gain -12 dB
  from canon.notes -> notes
}

master out {
  from voice.audio -> audio
}
{{/amplet}}

## Loops are bounded

Inside a helper you can repeat things, but every loop has a known end, so a song can never hang:

- `repeat 8 { ... }` and `for i in 0..8 { ... }` need a bound. `..` stops before the last number (0 to 7), `..=` includes it (0 to 8).
- `while condition max 128 { ... }` always has a `max`. Reaching it is an error, because a silently cut result would change the meaning.
- `emit x` adds notes to the result of a helper that returns notes.

{{deeper Where an expression is allowed}}
A property may be an expression when its type fits and it does not decide the shape of the song: `at`, `for`, the `from` lines and the names of blocks stay plain, so the layout never depends on a calculation. `block.property` reads the value another block ended up with. Equal values never rebuild the song: `gain -4dB` and an expression that works out to -4 dB give the same result.
{{/deeper}}

{{ample The note operations used in pipelines are also blocks. | transpose, quantize, midi-humanize, midi-clip}}
