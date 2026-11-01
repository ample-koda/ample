---
title: Loudness and limiting
order: 20
lead: Louder is not better. Learn what loudness is and why a limiter is the last tool.
---
## Peak and loudness

**Peak** is the highest point of the wave. **Loudness** is what you hear over time. Two songs with the same peak can be very different in loudness: one is dense, the other is dynamic.

## Limiter

A **limiter** is a very fast compressor that never lets the sound go above a ceiling. With it you can raise the whole song without clipping.

{{pv {"title":"Dynamic","src":"drums"}}}
{{pv {"title":"Pushed into a limiter-like squash","src":"drums","fx":"compressor","p":{"threshold":-30,"ratio":20}}}}

## LUFS

Streaming platforms measure loudness in LUFS and turn loud songs down. A song that is crushed to be loud gets no benefit; it only loses punch. A common target is around -14 LUFS for streaming.

{{deeper The loudness war and what it taught}}
From the 1990s, records were mastered louder and louder to stand out on the radio. The side effect: heavy compression and clipping that left little difference between loud and soft, and made the music tiring. Streaming services now normalise loudness (turn loud songs down), so the extra squash gains nothing but loses punch. The lesson: make the song sound good, then bring it to a reasonable level.
{{/deeper}}

{{deeper True peak and headroom}}
Digital peaks can exceed what a sample value shows once the sound is converted to analogue or compressed to a format like MP3. This **true peak** can be 1 dB or more above the highest sample. Leave about 1 dB of headroom (set the limiter ceiling to -1 dBTP) so the file does not distort after conversion.
{{/deeper}}

{{ample `limiter` and `normalize` finish a song. | limiter, normalize, meter}}
