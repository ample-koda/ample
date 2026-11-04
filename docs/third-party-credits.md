# Third-party credits

What AMPLE learned from, or uses, that is not its own. Dated, so it can be checked later.

The complete list, with the version of the time and what to watch for each item, is [where things came from](upstream-sources.md); this page keeps the full notices.

| What | Where in AMPLE | Source | Licence | How it is used |
|---|---|---|---|---|
| **MonkSynth** by Jonathan Taylor (c) 2026, a vocal synth after the Delay Lama of AudioNerdz (2002) | `src/Ample.Audio/Engine/DelayLlama.cs`, `web/lib/clientside-delay-llama.mjs` | https://github.com/JonET/monksynth | MIT | Read as a reference (2026-10-25) for the FOF (formant wave function, Rodet/IRCAM) principle, the vowel table (F1 280/450/800/350/270, F2 600/800/1150/2000/2140, F3 2240/2830/2900/2800/2950 Hz at a e i o u), the formant bandwidths (32.5/47.5/62.5 Hz), the 20 ms grain and its window, the two inharmonic breath components, the pitch steps of 1/32 semitone and the stereo echo taps (about 310 and 398 ms). The code, art and presets of AMPLE are its own: no source file was copied. |
| **Delay Lama** by AudioNerdz (2002) | the name and the idea of Delay Llama | http://www.audionerdz.nl/ | (inspiration only) | Nothing of it is used; the Llama is a new, native instrument. |
| **feminine singing repetitve chant melody** by buggly | the autotune demo of a project (not published here) | https://freesound.org/people/buggly/sounds/748552/ | CC0 | A demo voice; source, checksums and the conversion are in that folder's `LICENSES.md`. |

## The MIT notice of MonkSynth

MIT License. Copyright (c) 2026 Jonathan Taylor. Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files, to deal in the software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the software, subject to the inclusion of this copyright notice and this permission notice in all copies or substantial portions of the software. The software is provided "as is", without warranty of any kind.
