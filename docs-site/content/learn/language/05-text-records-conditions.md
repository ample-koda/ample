---
title: Text, records and conditions
order: 50
lead: Inside a helper you can make text from values, keep a few values together in a record, and choose with a condition.
---
## Making text

Three ways make text, and all three read a value the same way.

```ample
let a = "Bar " + 4                                  # "Bar 4"
let b = "Gain: " + String(-6dB)                     # "Gain: -6dB"
let c = sprintf("Bar %03d at %.1f%%", 4, 75.55)     # "Bar 004 at 75.6%"
```

- `+` joins a text with a text or a number, in either order. It only joins when one side is a text: `4 + 5` is still `9`, and `1 + 2 + "a"` is `"3a"`, because `+` works from left to right. Any other value next to a text (a quantity, a list, notes) is an error that tells you to write `String(...)`.
- `String(value)` turns one value into text: a number as written (`3`, `0.5`), a quantity with its unit (`75%`, `-6dB`, `7st`), a length in beats (`2bars` is `8beats` in 4/4), `true` or `false`, a word as itself. A list, a record or notes is refused: log the value itself to see it.
- `sprintf(format, values...)` fills placeholders in order: `%s` (any single value), `%d` (a whole number), `%f` and `%.2f` (a number with 6 or 2 decimals) and `%%` (a percent sign). A width and a flag work too: `%03d` gives `004`, `%-6s` pads a text on the right.

Numbers always read with a point for decimals, never a comma, whatever the language of the computer.

## Records

A record is a small value with named fields: `{steps: 3, label: "up"}`. A field is read with a dot, also one inside another: `plan.steps`, `plan.inner.a`. A record cannot be changed: make a new one. Its type is `Record`.

## Conditions

`if`, `while`, `!`, `&&`, `||` and `c ? a : b` read any value as true or false:

| Value | Counts as true when |
|---|---|
| a Bool | it is `true` |
| a number or a quantity | it is not 0 (`0ms` and `0%` are false) |
| a text, a word | it is not empty |
| a list, a range | it has items |
| notes | there is at least one note |

`a && b` and `a || b` give true or false and stop as soon as the answer is known. `c ? a : b` works out only the branch that is taken. The parentheses around the condition of an `if` are optional: `if shape == "up" { ... } else if ... { ... } else { ... }`.

{{amplet A helper that logs what it does, with a record and a condition}}
composition "Text and records" { ample 1  tempo 100  master-seed 20261017 }
settings { logging.level info }

helper arpeggio(Notes chord, Int steps) -> Notes {
  let plan = {steps: steps, label: "up"}
  log sprintf("arpeggio: %d steps, going %s", plan.steps, plan.label)
  log "the chord has " + count(chord) + " notes"
  plan.steps > 4 ? chord |> transpose(12) : chord
}

midi-clip chord at 1 for 1 {
  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100
  bars 1
}

midi-clip arp at 1 for 1 {
  notes arpeggio(chord.notes, 8)
  bars 1
}

synth voice at 1 for 1 {
  wave triangle
  gain -12 dB
  from arp.notes -> notes
}

master out {
  from voice.audio -> audio
}
{{/amplet}}

Text that only goes into a `log` line never changes the notes or the sound. Text that is used for the music (to choose a branch, as a property) counts like any other value. The log lines show in the Song log tab; the next page explains them.

{{ample Text, records and conditions are written inside helpers. | /learn/ample/language/expressions=Expressions, /learn/ample/language/messages-and-errors=Messages and errors}}
