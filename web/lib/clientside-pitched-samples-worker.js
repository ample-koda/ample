const clip = (value) => Math.max(-1, Math.min(1, value));

self.onmessage = ({ data }) => {
  try {
    const { samples: raw, notes, mode, gainDb, sampleRate, firstBar, bars, samplesPerBar } = data;
    const samples = raw.map((sample) => ({ pitch: sample.pitch, rate: sample.sourceRate / sampleRate, data: new Float32Array(sample.data) })).sort((a, b) => a.pitch - b.pitch);
    const output = new Float32Array(bars * samplesPerBar), gain = 10 ** (Math.max(-24, Math.min(48, Number(gainDb) || 0)) / 20);
    const nearest = (pitch) => samples.reduce((best, sample) => Math.abs(sample.pitch - pitch) < Math.abs(best.pitch - pitch) ? sample : best);
    const play = (pitch, velocity, start, length, scale) => {
      const sample = nearest(pitch), step = sample.rate * 2 ** ((pitch - sample.pitch) / 12), count = Math.min(Math.ceil(sample.data.length / step), length, output.length - Math.max(0, start));
      const amp = scale * gain * Math.max(0, Math.min(127, velocity)) / 96, fade = Math.min(Math.floor(sampleRate * 0.12), count);
      for (let i = 0; i < count; i++) {
        const pos = i * step, low = Math.floor(pos), high = Math.min(sample.data.length - 1, low + 1), mix = pos - low;
        const tail = fade > 0 && i > count - fade ? (count - i) / fade : 1, at = start + i;
        if (at >= 0) output[at] = clip(output[at] + ((sample.data[low] || 0) * (1 - mix) + (sample.data[high] || 0) * mix) * amp * tail);
      }
    };
    const inRange = notes.filter((note) => note.bar >= firstBar && note.bar < firstBar + bars);
    if (mode === 'bass') {
      const byBar = new Map();
      for (const note of inRange) if (!byBar.has(note.bar) || note.pitch < byBar.get(note.bar).pitch) byBar.set(note.bar, note);
      const beat = Math.floor(samplesPerBar / 4);
      for (const [bar, note] of byBar) for (let i = 0; i < 4; i++) play(note.pitch - 12, note.velocity, (bar - firstBar) * samplesPerBar + i * beat, beat + sampleRate / 8, 20000 / 32768);
    } else {
      for (const note of inRange) {
        const start = Math.floor((note.bar - firstBar + note.beat / 4) * samplesPerBar), length = Math.max(1, Math.floor(note.beats * samplesPerBar / 4)) + sampleRate / 4;
        play(note.pitch, note.velocity, start, length, 11000 / 32768);
      }
    }
    self.postMessage({ channel: output.buffer }, [output.buffer]);
  } catch (error) { self.postMessage({ error: error.message || 'Local sample preview failed.' }); }
};
