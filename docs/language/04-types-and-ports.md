# 04 Types and ports

A connection can be temporarily switched off without removing it: `from kit.audio -> in disabled`. The canvas retains it as a dimmed dashed line; its context menu enables it again. Disabled connections supply no input and do not create active build dependencies. Mixer inputs are optional; a mixer processes the active channels, or produces no take when none remain. Required consumers and audio/render effects with their main input switched off become implicitly disabled. Enabling a connection restores its original endpoints.

Every block template declares input ports and output ports, each with a type. The types of spec 0.1 are in `../../spec/types.json`:

| Type | Meaning |
| --- | --- |
| `audio` | rendered sound |
| `render` | an immutable source recipe with ordered effects, materialized when audio is requested or the effect is explicitly built |
| `midi` | notes and timing (textual MIDI) |
| `midi-sequence` | a bounded MIDI event stream with tick order |
| `text` | words, such as lyrics |
| `data` | structured data: style, external values |
| `any` | matches every type except `render`; used by groups |

## Connection rule

`from <block>.<output> -> <input>` connects when the output type and input type are equal, or either is `any`, except that `render` only connects to `render`. This keeps source recipes distinct from materialized audio even at group boundaries. There are no implicit conversions. A mismatch is `AMP2007`.

## Render recipes

Audio sources expose `audio` and `render` outputs. `audio` is the ordinary built sound. `render` carries an immutable recipe for that source and does not change its ordinary take. Effects accept either an active `audio` input or an active `render` input; both connections may be present when one or both are disabled. An effect can pass the recipe onward through `render` or materialize it at its existing audio output.

```text
drums.render -> eq.render -> reverb.render
reverb.audio -> mixer.in1
drums.audio -> mixer.in2
```

The effect chain is collected in order and the source is rendered with those effects when its audio output is consumed or the final effect is explicitly built. Intermediate render links create no takes. The mixer receives the effected variant on `in1` and ordinary drums on `in2`. Two render branches remain independent. Connecting `drums.audio -> eq.audio` keeps the existing post-render effect path.

The plan and cache identity include the source and its inputs, then the ordered effects and parameters. A changed effect rebuilds its variant and dependent audio consumers. `render` is not MIDI/events, and `any` cannot convert it.

## Port rules

- An input takes one source (`AMP2008` for a second). To combine several sources use a mixer-like block.
- A block cannot read itself (`AMP2009`); the graph must be acyclic (`AMP2010`, with the cycle path in the message).
- A required input with no source is lint rule L002 (a warning by default): the template marks which inputs are required.
- Naming a missing block, output or input is `AMP2004`, `AMP2005`, `AMP2006`; the hint lists the valid names.
- Output ports may stay unconnected. Unused outputs are lint rule L001 (info); audio that never reaches a `master` block is L007.

## Groups

A `group` block has one `any` input and one `any` output and may contain an inner graph that is edited in the node editor. Inner graphs are not part of the `.amplet` text in spec 0.1.
