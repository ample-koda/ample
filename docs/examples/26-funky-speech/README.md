# 26 Funk It Up

A 90 second funk song (106 BPM, E dorian, 40 bars: intro, verse, chorus, break, chorus, outro). The band is General MIDI: drums, slap bass, clavinet, muted guitar, electric piano and horns, all from the MuseScore General sound font (`pack:musescore-general`). The lyrics are sung by the local `speech` block in words mode: every note of a melody clip speaks the next word, and the pitch of the note sets the pitch of the voice.

Build it with the normal CLI:

```text
ample build docs/examples/26-funky-speech/funky-speech.amplet --out funk.mp3
```

What to look at:

- `speech ... { mode "words"  text "..."  from melody.notes -> notes }` and the melody clip next to it: one word per note.
- `duration_policy "fit"` speeds a word up (to at most 2x) when its note is short, then crops it.
- `gain 12` lifts the voice (a plain number of dB). The voice bus then goes through `autotune` (key `E dorian`, speed 10 ms: the robot-funk sound, with the vowels kept by the spectral method), a high-pass, an `eq` that lifts the highs above 2.8 kHz for clarity, a little soft `distortion` for bite, a short echo and a small room before the mix. Set `speed` to 40 and `amount` to 70% for a gentler correction, or `enabled off` to hear the plain voice.
- Every note stays inside its bar, as `midi-clip` notes must.

The speech renderer is currently server/CLI rendered; there is no browser-local speech preview yet.
