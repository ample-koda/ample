---
title: Messages and errors
order: 60
lead: A helper can say what it is doing, stop on purpose, raise and handle errors, and check its own code.
---
## Saying what a helper does: log, debug, trace, warn

`log "text"`, `debug value`, `trace value` and `warn "text"` show a message without changing the music. `debug` and `trace` show the value itself, so `debug count(notes)` shows a number. A message can carry a code, a hint and a record of details: `warn "The room is very large", code="ROOM_LARGE", context={size: size}`.

How much is shown is `logging.level` in the `settings { }` line of the song: `off`, `warning` (the default), `info`, `debug` or `trace`. A `warn` is always kept; `log`, `debug` and `trace` are only worked out when the level allows them, so a line that is switched off costs nothing.

The messages are in the **Song log** tab of the Log panel, which can also change the level for your session only. On the command line, `ample check song.amplet --log` prints them.

## Stopping on purpose: fail and require

- `fail "message"` stops the block: it gets no notes and the message is shown as the error. Use it for a state that must never happen.
- `require condition` checks what a helper is given: `require voices > 0, "voices must be positive"`. Without a text the message is the condition as you wrote it.

## Errors: throw, try, catch, finally

`throw "message"` raises an error; `try { ... } catch (e) { ... } finally { ... }` handles it. A `catch` can name the kind it handles (`catch (RequirementError e)`, the kind a failed `require` raises), or none. `finally` always runs, also when the error is not handled.

Your own kind of error has typed fields: `error UnknownShapeError extends Error { String shape }`. Make one with the message first and the fields by name: `throw UnknownShapeError("There is no such shape", shape=name)`. In the `catch`, `e.shape` reads the field back.

A `catch` handles `throw`, a failed `require` and errors that depend on values (a division by zero, a field that is not there). It never handles `fail`, a mistake in the code, or a safety stop (a loop that runs too long).

## Checking your own code: assert

`assert condition` states something that can only be false through a mistake in the code. Assertions are off by default and then cost nothing; switch them on with `settings { diagnostics.assertions on }` or with the Assertions switch of the Song log tab.

Use `require` for input a caller can fix, `assert` for a bug, `throw` for an error with its own kind and data, and `fail` to stop on purpose.

{{amplet A helper that raises its own error, and one that handles it}}
composition "Messages and errors" { ample 1  tempo 100  master-seed 20261017 }
settings { logging.level info  diagnostics.assertions on }

error UnknownShapeError extends Error { String shape }

helper shaped(Notes source, String shape) -> Notes {
  require count(source) > 0, "the source has no notes"
  assert count(source) < 1000
  if shape == "up" { return source |> transpose(12) }
  else if shape == "down" { return source |> transpose(-12) }
  else { throw UnknownShapeError("There is no such shape", shape=shape) }
}

helper safeShape(Notes source, String shape) -> Notes {
  try {
    return shaped(source, shape)
  } catch (UnknownShapeError e) {
    warn "Unknown shape, the notes stay as they are", code="SHAPE_UNKNOWN", context={shape: e.shape}
    return source
  } finally {
    debug shape
  }
}

midi-clip theme at 1 for 1 {
  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100
  bars 1
}

midi-clip line at 1 for 1 {
  notes safeShape(theme.notes, "sideways")
  bars 1
}

synth voice at 1 for 1 {
  wave triangle
  gain -12 dB
  from line.notes -> notes
}

master out {
  from voice.audio -> audio
}
{{/amplet}}

This song plays the theme unchanged and shows one warning, `Unknown shape, the notes stay as they are [SHAPE_UNKNOWN]`, with the shape that was asked for.

{{deeper Where the messages come from}}
Every message has a fixed code, so a tool can filter on it: a `warn` is AMP6001, a `fail` AMP6002, an error that nobody handled AMP6003, a failed `assert` AMP6004 and a failed `require` AMP6005. A failure also says which helpers were running when it happened, innermost first. A message never shows the value of a secret: it shows `[redacted]` instead.
{{/deeper}}

{{ample The same statements work in the process of a component. | /learn/ample/language/text-records-conditions=Text and records, /learn/ample/components/what-is-a-component=Components}}
