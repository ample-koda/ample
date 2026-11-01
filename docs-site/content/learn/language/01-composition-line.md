---
title: The composition line
order: 10
lead: Every song starts with one line that names the song and fixes its tempo, its meter and its seed.
---
## The line

`composition "porch song" { ample 1  tempo 90  meter 3/4  master-seed 20261017 }`

| Part | What it says |
|---|---|
| `composition "name"` | The name of the song (the word `project` also works). |
| `ample 1` | The language version the song was written for. Optional. |
| `tempo 90` | Quarter notes per minute, from 20 to 300. Always counts quarter notes, in every meter. |
| `meter 3/4` | The time signature. Optional, 4/4 without it. |
| `master-seed 20261017` | The seed that every random choice in the song comes from. |

`tempo` and `master-seed` are required, `ample` and `meter` are optional, and the order is the one in the table. A line that starts with `#` is a comment.

## Tempo and meter

A bar has the beats of its meter: 3/4 and 6/8 have three quarter-note beats, 5/4 has five, 7/8 has three and a half. The top number can be 1 to 16 and the bottom number 2, 4, 8 or 16. Anything else is reported as AMP1120 and the song stays in 4/4. Notes then use beat 0 up to the beats of the bar, so in 3/4 the beats are 0, 1 and 2.

{{amplet A waltz in 3/4: two bars, a low note and two high ones in each}}
composition "waltz" { ample 1  tempo 96  meter 3/4  master-seed 20261017 }

midi-clip waltz at 1 for 2 {
  notes 1:0:1:48:100 1:1:1:64:70 1:2:1:67:70 2:0:1:43:100 2:1:1:64:70 2:2:1:67:70
  bars 2
}

synth voice at 1 for 2 {
  wave triangle
  gain -10 dB
  from waltz.notes -> notes
}

master out {
  from voice.audio -> audio
}
{{/amplet}}

## The master seed

Randomness in AMPLE is never really random: every random choice (a humanized timing, a noise burst, a generated take) is derived from the master seed, so the same song text always gives the same sound. The seed is a whole number from 0 to 18446744073709551615. Change it and the random parts change; keep it and a copy of the song sounds exactly like the original. The sounds in this documentation all use the seed `20261017`, so that you can copy them and hear what the page played.

{{deeper What a meter does not do yet}}
A meter belongs to the whole song: it cannot change inside a song, and a clip cannot have a meter of its own. MIDI and MusicXML files are read and written with the meter of the file, and the song that uses the imported module must say the same meter.
{{/deeper}}

{{ample The Music course explains what a time signature is and plays the common ones. | /learn/music/basic/time-signatures=Time signatures, midi-clip}}
