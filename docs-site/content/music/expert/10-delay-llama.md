---
title: Singing voices and the Delay Llama
order: 85
lead: How a voice makes vowels, and how a synthesiser can sing without a recording.
---
## Why a vowel sounds like a vowel

When you sing "aaah" and then "eeee" on the same note, the pitch does not change. What changes is the shape of your mouth, which boosts certain frequency bands. These bands are the **formants**. Each vowel has its own pair of strong formants, and your ear recognises the vowel from them.

| Vowel | Rough position of the two main formants | Mouth |
|---|---|---|
| a (as in "father") | about 800 Hz and 1200 Hz | wide open |
| e (as in "bed") | about 400 Hz and 2200 Hz | half open, smile |
| i (as in "see") | about 300 Hz and 2500 Hz | small, smile |
| o (as in "go") | about 500 Hz and 900 Hz | round |
| u (as in "too") | about 300 Hz and 800 Hz | small, round |

{{pv {"title":"A scripted singer moving through vowels (rough stand-in)","src":"voice","note":"Filters in the browser stand in for the formants. The real block is smoother and has a breath and vibrato."}}}

## A voice from a recipe

The `delay-llama` block builds a voice from formants (a method called FOF synthesis), so you get a humming, oohing and aahing voice from plain notes. You steer it with a few settings, which can be a number or a curve over the song:

| Setting | What it changes |
|---|---|
| vowel | 0 is "a", 25 "e", 50 "i", 75 "o", 100 "u", and everything between |
| voice | the size of the singer: low is deep and big, high is small and bright |
| mouth | closed is a hum, open is a full "aah" |
| glide | how long it slides from one note to the next |
| vibrato | how deep the vibrato is |
| breath | how much air is in the voice |
| unison | how many voices sing the same note: a choir of llamas |
| echo | the classic stereo echo of the Delay Lama |

{{try Make it say something}}
Give `vowel` a curve that moves from 0 to 100 over four bars, and let `mouth` open at the same time. Play a long note. You will hear the voice go from a wide "aaah" to a closed "ooo".
{{/try}}

{{deeper Formants and pitch are separate}}
The pitch comes from how fast the vocal cords vibrate. The formants come from the shape of the throat and mouth. That is why a child and an adult can sing the same note and sound different: the formants sit higher for a smaller vocal tract. The `voice` setting of the block changes exactly this size without changing the note.
{{/deeper}}

{{ample Play notes into a `delay-llama` and automate its vowel with a curve. | delay-llama, curve, /learn/ample/tour/sources-and-instruments=Block tour: instruments}}
