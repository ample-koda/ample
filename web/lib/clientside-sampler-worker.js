self.onmessage = ({ data }) => {
  try {
    const { channels: sampleBuffers, sourceRate, notes, params, sampleRate, firstBar, bars, samplesPerBar } = data;
    const samples = sampleBuffers.map((buffer) => new Float32Array(buffer)), outputFrames = bars * samplesPerBar, out = samples.map(() => new Float32Array(outputFrames));
    if (params.enabled === 'off') { self.postMessage({ channels: out.map((channel) => channel.buffer) }, out.map((channel) => channel.buffer)); return; }
    const root = Math.max(0, Math.min(127, Number(params.root) || 60)), gain = 10 ** (Math.max(-36, Math.min(48, Number.parseFloat(params.gain) || 0)) / 20), hold = params.mode === 'hold';
    const release = Math.max(1, Number.parseFloat(params.release) || 30) * sampleRate / 1000;
    for (const note of notes) {
      if (note.bar < firstBar || note.bar >= firstBar + bars) continue;
      const start = Math.floor((note.bar - firstBar + note.beat / 4) * samplesPerBar), semitones = Math.max(-36, Math.min(36, note.pitch - root));
      const step = 2 ** (semitones / 12) * sourceRate / sampleRate, noteLength = Math.max(1, Math.floor(note.beats * samplesPerBar / 4));
      const length = hold ? Math.min(Math.ceil(samples[0].length / step), noteLength) : Math.ceil(samples[0].length / step), velocity = note.velocity / 127;
      for (let i = 0; i < length && start + i < outputFrames; i++) {
        const sourceAt = i * step, at = start + i, fade = hold && i > length - release ? Math.max(0, (length - i) / release) : 1;
        for (let channel = 0; channel < samples.length; channel++) {
          const source = samples[channel], low = Math.floor(sourceAt), high = Math.min(source.length - 1, low + 1), fraction = sourceAt - low;
          out[channel][at] = Math.max(-1, Math.min(1, out[channel][at] + ((source[low] || 0) * (1 - fraction) + (source[high] || 0) * fraction) * gain * velocity * fade));
        }
      }
    }
    self.postMessage({ channels: out.map((channel) => channel.buffer) }, out.map((channel) => channel.buffer));
  } catch (error) { self.postMessage({ error: error.message || 'Local sampler rendering failed.' }); }
};
