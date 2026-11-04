# Preview playback parity scenario

1. A completed build exposes play, stop, waveform scrubbing and WAV/MP3 download choices without requiring playback to have started.
2. A preview uses the same controller and can be stopped and scrubbed while it plays.

1. Preview an audio-producing block whose upstream result is missing.
2. Expect missing work to build first and cached work to pass without rendering.
3. Play, seek, loop and stop, and expect controls to refer to the stored preview take.
