self.onmessage = ({ data }) => {
  try {
    const { notes, sampleRate, firstBar, bars, samplesPerBar } = data, output = new Float32Array(bars * samplesPerBar);
    for (const note of notes) {
      if (note.bar < firstBar || note.bar >= firstBar + bars) continue;
      const start = Math.floor((note.bar - firstBar + note.beat / 4) * samplesPerBar), length = Math.max(1, Math.floor(note.beats * samplesPerBar / 4));
      const frequency = 440 * 2 ** ((note.pitch - 69) / 12);
      for (let i = 0; i < length && start + i < output.length; i++) {
        const phase = ((i * frequency / sampleRate) % 1 + 1) % 1, triangle = phase < 0.5 ? phase * 4 - 1 : 3 - phase * 4, saw = phase * 2 - 1;
        output[start + i] = Math.max(-1, Math.min(1, output[start + i] + ((triangle + saw) * 0.1375 * (length - i) / length)));
      }
    }
    self.postMessage({ channel: output.buffer }, [output.buffer]);
  } catch (error) { self.postMessage({ error: error.message || 'Local piano rendering failed.' }); }
};
