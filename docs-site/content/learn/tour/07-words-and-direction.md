---
title: Words and direction
order: 70
lead: Lyrics and the mood of a part: information that other blocks can read.
---
## Lyrics

A song can have a lyrics file with timed words. The `lyrics` block gives that text to blocks that need it (such as voices).

{{blocks lyrics}}

## Direction

A `mood-scene` describes a reusable musical scene and its emotion: for example "a calm morning, slow, warm". Blocks that accept style data can read it.

{{blocks mood-scene; choir}}

## AI voices and music

Some blocks use AI models (a voice, a full song, a cloned voice, a scorer). They are optional plugins that you switch on per song, and they are not described on these pages yet.
