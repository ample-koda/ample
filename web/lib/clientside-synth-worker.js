self.onmessage = ({ data }) => {
  try {
    const { notes, params, sampleRate: rate, firstBar, bars, samplesPerBar } = data;
    const frames = bars * samplesPerBar, left = new Float32Array(frames), right = new Float32Array(frames);
    if (params.enabled === 'off') { self.postMessage({ channels: [left.buffer, right.buffer] }, [left.buffer, right.buffer]); return; }
    const attack = Math.max(1, parameter(params.attack, 10)) * rate / 1000;
    const decay = Math.max(1, parameter(params.decay, 200)) * rate / 1000;
    const sustain = Math.max(0, Math.min(100, Number(params.sustain ?? 70))) / 100;
    const release = Math.max(1, parameter(params.release, 250)) * rate / 1000;
    const detune = Math.max(0, Math.min(50, Number(params.detune) || 0));
    const gain = 10 ** (Math.max(-36, Math.min(48, parameter(params.gain, -6))) / 20);
    const cutoff = Math.max(40, Math.min(20000, parameter(params.cutoff, 8000)));
    const wave = ['sine', 'triangle', 'saw', 'square', 'noise'].includes(params.wave) ? params.wave : 'saw';
    for (const note of notes) {
      if (note.bar < firstBar || note.bar >= firstBar + bars) continue;
      const start = Math.max(0, Math.floor((note.bar - firstBar + note.beat / 4) * samplesPerBar));
      const held = Math.max(1, Math.floor(note.beats * samplesPerBar / 4));
      const end = Math.min(frames, start + held + Math.ceil(release * 8));
      const hz = 440 * 2 ** ((note.pitch - 69) / 12), velocity = note.velocity / 127;
      const pan = Math.max(-1, Math.min(1, (note.pan || 0) / 100));
      const lGain = Math.cos((pan + 1) * Math.PI / 4), rGain = Math.sin((pan + 1) * Math.PI / 4);
      let lpL = 0, lpR = 0, lfsr = 0x7FFF, cycles = 0;
      const alpha = 1 - Math.exp(-2 * Math.PI * Math.min(cutoff, rate * 0.49) / rate);
      for (let i = 0; start + i < end; i++) {
        let env;
        if (i < held) env = i < attack ? i / attack : i < attack + decay ? 1 - (1 - sustain) * (i - attack) / decay : sustain;
        else env = sustain * Math.max(0, 1 - (i - held) / release);
        const t = i / rate, f = hz * 2 ** ((note.fine || 0) / 1200);
        let a, b;
        if (wave === 'noise') {
          // the same shift register as the server: clocked once per cycle of the pitch
          for (const wrapped = Math.floor(t * f); cycles < wrapped; cycles++) lfsr = (lfsr >> 1) | (((lfsr ^ (lfsr >> 1)) & 1) << 14);
          a = b = (lfsr & 1) ? 0.61 : -0.61;
        } else { a = oscillator(wave, t * f); b = detune > 0 ? oscillator(wave, t * f * 2 ** (detune / 1200)) : a; }
        const sample = (a + b) * 0.5 * env * velocity * gain * 0.22;
        lpL += alpha * (sample * lGain - lpL); lpR += alpha * (sample * rGain - lpR);
        left[start + i] += lpL; right[start + i] += lpR;
      }
    }
    self.postMessage({ channels: [left.buffer, right.buffer] }, [left.buffer, right.buffer]);
  } catch (error) { self.postMessage({ error: error.message || 'Local synth rendering failed.' }); }
};

function parameter(value, fallback) {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function oscillator(wave, phase) {
  const cycle = phase - Math.floor(phase);
  if (wave === 'square') return cycle < 0.5 ? 1 : -1;
  if (wave === 'triangle') return 1 - 4 * Math.abs(cycle - 0.5);
  if (wave === 'sine') return Math.sin(2 * Math.PI * cycle);
  return 2 * cycle - 1;
}
