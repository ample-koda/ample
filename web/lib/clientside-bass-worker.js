self.onmessage = ({ data }) => {
  try {
    const { notes, sampleRate, firstBar, bars, samplesPerBar } = data, frames = bars * samplesPerBar, output = new Float32Array(frames), beat = Math.floor(samplesPerBar / 4);
    const byBar = new Map();
    for (const note of notes) if (note.bar >= firstBar && note.bar < firstBar + bars) byBar.set(note.bar, Math.min(byBar.get(note.bar) ?? 128, note.pitch));
    for (const [bar, pitch] of byBar) {
      const frequency = 440 * 2 ** ((pitch - 12 - 69) / 12), start = (bar - firstBar) * samplesPerBar;
      for (let count = 0; count < 4; count++) for (let i = 0; i < beat; i++) {
        const at = start + count * beat + i;
        output[at] += (Math.sin(2 * Math.PI * frequency * at / sampleRate) >= 0 ? 0.5 : -0.5) * (beat - i) / beat;
      }
    }
    self.postMessage({ channel: output.buffer }, [output.buffer]);
  } catch (error) { self.postMessage({ error: error.message || 'Local bass rendering failed.' }); }
};
