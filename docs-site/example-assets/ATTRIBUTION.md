# Free files of the example songs

The example songs of some block pages need a file (a recording, a MIDI file, a SoundFont, note recordings). The files in `files/` are the ones the docs use to play and show those examples. All of them are CC0 1.0 (public domain dedication) or made for AMPLE; credit is not required, but is given here. Licences were checked on the source page on 2026-11-13. `examples.json` says which file each block uses and at which project path; the build copies `files/` to `site/example-assets/`.

| File | Used by | Source and licence |
|---|---|---|
| `rain-room.wav` | ambience (`recordings/room-tone.wav`) | First 16 s of "Light rain on street.wav" by BonnyOrbit, https://freesound.org/people/BonnyOrbit/sounds/380651/ , CC0. Mono, 22.05 kHz, 1 s fade out. |
| `minuet-synth.wav` | import-audio (`recordings/preview.wav`) | First 12 s of "Minuet in G major, BWV Anh. 114 (played by PLATO GSW)" (Christian Petzold, arranged by Marshall Midden, played on a PLATO terminal with the Gooch Synthetic Woodwind synthesizer; uploaded by Morn), https://commons.wikimedia.org/wiki/File:Minuet_in_G_major,_BWV_Anh._114_(played_by_PLATO_GSW).flac , CC0 1.0 on the file page. Mono, 44.1 kHz, 0.5 s fade out. |
| `guitar-note-c4.wav` | sampler (`recordings/sampler-preview.wav`) | Note c4 of the Emily guitar (Karoryfer Samples, D. Smolken), https://github.com/sfzinstruments/karoryfer.emilyguitar , CC0-1.0. Cut to 3 s, mono, 32 kHz, fade out. |
| `guitar/*.wav` | guitar (`samples/guitar/`) | Nine notes (a2 a3 a4 c3 c4 e2 eb3 gb2 gb3) of the same Emily guitar, CC0-1.0. Cut to 2.5 s, mono, 22.05 kHz, fade out. |
| `minuet-g.mid` | midi, midi-render (`midi/input.mid`) | "Nannerl Notenbuch - Minuet in G" (Mozart, 1762, public-domain composition; MIDI by Turbotoaster, 2010, released into the public domain by the uploader), https://commons.wikimedia.org/wiki/File:Nannerl_Notenbuch_-_Minuet_in_G.mid , 1.7 kB, unchanged. (Commons has a newer "(corr)" version that fixes a small transcription error, but it is CC BY-SA, so the public-domain original is used.) |

Other things the examples use: bass (`set analog-808`) and piano (`set felt-piano`) are built-in sounds and need no file. convolution uses `ir room`, a synthetic impulse response of the CC0 pack `impulse-responses` (made by `tools/make-impulse-responses.mjs`; `ample packs install impulse-responses`).

gm and midi-render use the language default `pack:musescore-general`, the MuseScore General SoundFont (MIT, S. Christian Collins; see `LICENSES/musescore-general.txt`). It is not shipped on the site: the page says to run `ample packs install soundfont` (pinned by SHA-256). The renderer reads it from the user-level audio packs. MIT material is accepted for the docs examples.

lyrics has no example song with sound: the block makes no sound, so its page shows the spec example without a player.

mood-scene and lyrics have no example song with sound (the page shows the spec example without a player). `rev` in `examples.json` renames the mp3 of an example whose files changed.
