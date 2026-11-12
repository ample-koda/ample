// The voice of the `synth` block as a streaming core (plan local-preview 3.3): one note is a SynthVoice that adds its samples to a left and right buffer, a block at a time or all at
// once. The offline render (clientside-synth-worker.js) and the live AudioWorklet (synth-worklet.mjs) both run this class, so the sound of a note on the piano roll is the sound of
// the preview, sample for sample. The settings are read when a note starts (as the render does). Pure: no Web Audio.

const parameter = (value, fallback) => {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : fallback;
};

function oscillator(wave, phase) {
  const cycle = phase - Math.floor(phase);
  if (wave === 'square') return cycle < 0.5 ? 1 : -1;
  if (wave === 'triangle') return 1 - 4 * Math.abs(cycle - 0.5);
  if (wave === 'sine') return Math.sin(2 * Math.PI * cycle);
  return 2 * cycle - 1;
}

/** The numbers of a synth block's parameters at a sample rate: envelope in samples, sustain 0..1, detune in cents, linear gain, cutoff in Hz, the wave. */
export function synthSettings(params, rate) {
  return {
    rate, enabled: params.enabled !== 'off',
    attack: Math.max(1, parameter(params.attack, 10)) * rate / 1000,
    decay: Math.max(1, parameter(params.decay, 200)) * rate / 1000,
    sustain: Math.max(0, Math.min(100, Number(params.sustain ?? 70))) / 100,
    release: Math.max(1, parameter(params.release, 250)) * rate / 1000,
    detune: Math.max(0, Math.min(50, Number(params.detune) || 0)),
    gain: 10 ** (Math.max(-36, Math.min(48, parameter(params.gain, -6))) / 20),
    cutoff: Math.max(40, Math.min(20000, parameter(params.cutoff, 8000))),
    wave: ['sine', 'triangle', 'saw', 'square', 'noise'].includes(params.wave) ? params.wave : 'saw',
  };
}

/** One note: pitch (MIDI), velocity 1..127, held (samples the key is down), pan -100..100, fine (cents). length is how many samples the note sounds in all (the release tail included). */
export class SynthVoice {
  constructor(settings, note) {
    this.s = settings; this.i = 0;
    const { rate, release } = settings;
    this.held = Math.max(1, Math.floor(note.held));
    this.length = this.held + Math.ceil(release * 8);
    this.hz = 440 * 2 ** ((note.pitch - 69) / 12); this.velocity = note.velocity / 127;
    const pan = Math.max(-1, Math.min(1, (note.pan || 0) / 100));
    this.lGain = Math.cos((pan + 1) * Math.PI / 4); this.rGain = Math.sin((pan + 1) * Math.PI / 4);
    this.fine = note.fine || 0;
    this.lpL = 0; this.lpR = 0; this.lfsr = 0x7FFF; this.cycles = 0;
    this.alpha = 1 - Math.exp(-2 * Math.PI * Math.min(settings.cutoff, rate * 0.49) / rate);
  }

  get done() { return this.i >= this.length; }

  /** Adds the next samples of the note to left[from..to) and right[from..to); stops by itself at the end of the tail. */
  process(left, right, from, to) {
    const { rate, attack, decay, sustain, release, detune, gain, wave } = this.s;
    const { held, hz, velocity, lGain, rGain, alpha } = this;
    for (let n = from; n < to && this.i < this.length; n++, this.i++) {
      const i = this.i;
      let env;
      if (i < held) env = i < attack ? i / attack : i < attack + decay ? 1 - (1 - sustain) * (i - attack) / decay : sustain;
      else env = sustain * Math.max(0, 1 - (i - held) / release);
      const t = i / rate, f = hz * 2 ** (this.fine / 1200);
      let a, b;
      if (wave === 'noise') {
        // the same shift register as the server: clocked once per cycle of the pitch
        for (const wrapped = Math.floor(t * f); this.cycles < wrapped; this.cycles++) this.lfsr = (this.lfsr >> 1) | (((this.lfsr ^ (this.lfsr >> 1)) & 1) << 14);
        a = b = (this.lfsr & 1) ? 0.61 : -0.61;
      } else { a = oscillator(wave, t * f); b = detune > 0 ? oscillator(wave, t * f * 2 ** (detune / 1200)) : a; }
      const sample = (a + b) * 0.5 * env * velocity * gain * 0.22;
      this.lpL += alpha * (sample * lGain - this.lpL); this.lpR += alpha * (sample * rGain - this.lpR);
      left[n] += this.lpL; right[n] += this.lpR;
    }
  }
}

/**
 * The offline render of a note list (what the build preview does): notes are { bar, beat, beats, pitch, velocity, pan, fine }; returns [left, right] Float32Arrays of bars * samplesPerBar.
 */
export function renderSynthNotes(notes, params, rate, firstBar, bars, samplesPerBar) {
  const frames = bars * samplesPerBar, left = new Float32Array(frames), right = new Float32Array(frames);
  if (params.enabled === 'off') return [left, right];
  const settings = synthSettings(params, rate);
  for (const note of notes) {
    if (note.bar < firstBar || note.bar >= firstBar + bars) continue;
    const start = Math.max(0, Math.floor((note.bar - firstBar + note.beat / 4) * samplesPerBar));
    const voice = new SynthVoice(settings, { pitch: note.pitch, velocity: note.velocity, held: Math.floor(note.beats * samplesPerBar / 4), pan: note.pan, fine: note.fine });
    // the original: end = min(frames, start + held + ceil(release * 8)) with held = max(1, floor(...)) (the voice computes the same length)
    voice.process(left, right, start, Math.min(frames, start + voice.length));
  }
  return [left, right];
}
