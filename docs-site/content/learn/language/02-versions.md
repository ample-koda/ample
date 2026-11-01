---
title: Versions
order: 20
lead: A song says which AMPLE it was written for, and a newer AMPLE builds an older song as it was.
---
## The language version

The `ample 1` on the composition line is the language version the song was written for. It is optional. When AMPLE is newer, the song still builds as it was written, and the text only changes when you ask it to (`ample migrate`), with a preview of what will change.

## What a version number promises

- A **patch** change (a wording fix) does not change the version.
- A **minor** version adds things in a way that keeps older songs working: a new block, a new optional parameter, a new diagnostic.
- A **major** version may break compatibility and comes with a migration note.
- Before the specification is settled, a minor version may break; every break has a migration note.

The specification is at version 1.0 with the status **provisional**. When it becomes settled, a song that is valid in 1.x stays valid in every later 1.y and builds to the same sound; a feature that is retired keeps working with a warning for at least one minor version.

## Blocks have versions too

Every block says since which version it exists and whether it is `provisional`, `stable`, `deprecated` or `retired`. You can ask for a version of a block, or of a component, with `pinned@` after its name. Without it a block follows the current version.

{{amplet A block pinned to version 1}}
composition "Pinned" { ample 1  tempo 100  master-seed 20261017 }

midi-clip riff at 1 for 1 {
  melody "c4:1 e4:1 g4:1 r:1"
  bars 1
}

synth lead pinned@1 at 1 for 1 {
  wave triangle
  gain -12 dB
  from riff.notes -> notes
}

master out {
  from lead.audio -> audio
}
{{/amplet}}

{{deeper What never changes}}
Numbers of diagnostics and lint codes are never reused or renumbered. Block ids are never reused. The way a plan is written down and the random generator are frozen once released and carry their own version, so an old song keeps drawing the same random numbers. Files such as songs, lyrics and exports carry a format version too, and opening an older one runs a migration after making a backup copy.
{{/deeper}}

{{ample Components have versions with pins and seals of their own. | /learn/ample/components/versions-pins-seals=Versions and pins of components}}
