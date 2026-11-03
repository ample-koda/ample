# Recording your voice

The arrangement panel has a **Record...** button. It opens a small recorder that works on your own computer and only sends something to the AMPLE server when you say so.

## Steps
1. **Choose the microphone.** The level meter moves when the browser hears sound. The first time, the browser asks for permission; if it was blocked, allow the microphone in the address bar and open the recorder again. The page must be `localhost` or HTTPS.
2. **Record.** Optional: a count-in of four clicks at the tempo of the song, and playing the built song while you sing (put on headphones, or the microphone hears the music). The recording is a raw take: the browser's noise suppression, echo cancellation and automatic gain are off unless you switch them on.
3. **Listen, trim, retake.** Every recording is a take in the list. Select one to see its waveform, move the start and end sliders to trim, tick *normalise* to bring the peak to 90 percent, and press *Listen*. Nothing has left your browser yet.
4. **Latency.** Monitoring through the browser adds a small delay, so a voice recorded to the song lands slightly late. Type the delay in milliseconds, or press *Calibrate latency*: it plays a click through the speakers and listens for it, so keep the microphone near the speaker. The number is subtracted from the start of the take.
5. **Use in project.** The chosen take (with trim, latency and normalise applied) is uploaded as a 16-bit mono WAV into the project folder `recordings/` (an existing name gets a new one) and an `import-audio` block called `recording` points at it. The upload goes through the normal project upload of the server, so it also works when the server runs on another computer.

## After recording
Clean the voice with blocks: a `gate` removes the noise between phrases, a `deesser` tames harsh s sounds, then a `compressor`, `eq` and `reverb`. Everything after the upload is ordinary AMPLE, so the take is stored in the project and reproducible.

## Limits
- The count-in clicks are scheduled on the audio clock and the take is cut at the frame of the downbeat (bar 1 of the song), so the count-in is not in the take and the start is exact on the clock of the microphone. The built song is started with a plain play call at that moment (a browser audio element cannot be scheduled), so what is left is the delay between the play call and the sound coming out of your speakers; the latency number (or *Calibrate latency*, which now measures from the frame the click was scheduled at) covers that.
- Only one take is uploaded at a time.
