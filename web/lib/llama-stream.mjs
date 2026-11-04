// Delay Llama as a live voice: the same FOF voice as the engine (DelayLlama.cs) and as clientside-delay-llama.mjs, but streaming: it is asked for a block of samples at a time and keeps its
// state between the blocks (phases, envelope, the grains that still ring), so notes can start and stop at any moment, in real time, without anything being rendered in advance.
// No browser objects in here: the AudioWorklet (llama-worklet.mjs) feeds it, and the tests run it in node. @ample:delay-llama

import { DEFAULTS, formant } from './clientside-delay-llama.mjs';

const BANDWIDTHS = [32.5, 47.5, 62.5];
const RING = 8192;
const soft = (x) => (Math.abs(x) < 0.8 ? x : Math.sign(x) * (0.8 + 0.2 * Math.tanh((Math.abs(x) - 0.8) / 0.2)));

class Voice {
  constructor(rate, seed, cents, pan, tables) {
    Object.assign(this, { rate, cents, pan, tables, ring: new Float64Array(RING), pos: 0, stage: 0, envelope: 0, level: 0, pitch: 60, glideFrom: NaN, glideTo: NaN, glideStart: 0, noteSince: 0, sinceGrain: Number.MAX_VALUE, jitter: 1, jitterCounter: 0 });
    this.rng = ((seed >>> 0) | 1) >>> 0;
    this.vibratoPhase = this.random() * 2 * Math.PI;
  }
  random() { this.rng = (Math.imul(this.rng, 1103515245) + 12345) >>> 0; return ((this.rng >>> 16) & 0x7fff) / 32768; }
  // a note: with legato (a note is still sounding) the voice glides to it without a new attack
  on(target, velocity, legato, s, sample) {
    if (legato && s.glideMs > 0 && this.stage >= 1 && this.stage <= 3) { this.glideFrom = this.pitch; this.glideTo = target; this.glideStart = sample; }
    else { this.pitch = target; this.glideFrom = this.glideTo = NaN; this.stage = 1; this.noteSince = sample; }
    this.level = 0.35 + 0.65 * velocity / 127;
  }
  off() { if (this.stage !== 0) this.stage = 4; }
  // one sample: the envelope, the glide, the vibrato and, once a period, a new grain laid into the ring; returns what rings now
  step(sample, s, ctl, bend) {
    const rate = this.rate, t = this.tables;
    if (!Number.isNaN(this.glideTo)) {
      const u = Math.min(1, (sample - this.glideStart) / Math.max(1, s.glideMs * rate / 1000));
      this.pitch = this.glideFrom + (this.glideTo - this.glideFrom) * (u * u * (3 - 2 * u));
      if (u >= 1) this.glideFrom = this.glideTo = NaN;
    }
    if (this.stage === 1) { this.envelope += 1 / Math.max(1, s.attackMs * rate / 1000); if (this.envelope >= 1) { this.envelope = 1; this.stage = 2; } }
    else if (this.stage === 2) { this.envelope -= (1 / Math.max(1, s.decayMs * rate / 1000)) * (1 - s.sustain); if (this.envelope <= s.sustain) { this.envelope = s.sustain; this.stage = 3; } }
    else if (this.stage === 4) { this.envelope -= 1 / Math.max(1, s.releaseMs * rate / 1000); if (this.envelope <= 0) { this.envelope = 0; this.stage = 0; } }
    if (++this.jitterCounter >= t.jitterPeriod) { this.jitterCounter = 0; this.jitter = 0.9 + this.random() * 0.2; }
    this.vibratoPhase += 2 * Math.PI * s.vibratoHz * this.jitter / rate;
    if (this.stage !== 0) {
      const onset = Math.min(1, (sample - this.noteSince) / (rate * 0.3));
      const vibrato = (0.04 + s.vibrato * 0.6) * onset * Math.sin(this.vibratoPhase);
      const sung = this.pitch + this.cents / 100 + vibrato + bend;
      const quantised = Math.floor((sung - 12) * 32) / 32 + 12;
      const period = Math.max(1, rate / (440 * 2 ** ((quantised - 69) / 12)));
      this.sinceGrain++;
      if (this.sinceGrain >= period) {
        const overshoot = Math.min(1, this.sinceGrain - period);
        this.sinceGrain = overshoot;
        const open = Math.min(1, Math.max(0, ctl.mouth)), scale = ctl.voice * 0.5 + 0.75;
        const f1 = formant(0, ctl.vowel) * scale * (0.75 + 0.45 * open), f2 = formant(1, ctl.vowel) * scale, f3 = formant(2, ctl.vowel) * scale;
        const breathAmount = s.breath * (1.2 - 0.7 * open), compensate = Math.min(3, Math.max(0.1, (sung - 12) * (-1 / 72) + 2));
        const amplitude = 0.5 * compensate * this.envelope * this.level * (0.45 + 0.55 * open);
        for (let k = 0; k < t.grain; k++) {
          const u = (k + overshoot) / rate;
          const sample1 = Math.sin(2 * Math.PI * f1 * u) * Math.exp(-Math.PI * BANDWIDTHS[0] * u) + Math.sin(2 * Math.PI * f2 * u) * Math.exp(-Math.PI * BANDWIDTHS[1] * u) * (0.6 + 0.4 * open)
            + Math.sin(2 * Math.PI * f3 * u) * Math.exp(-Math.PI * BANDWIDTHS[2] * u) * 0.8 + t.breath[k] * breathAmount;
          this.ring[(this.pos + k) % RING] += sample1 * t.window[k] * amplitude;
        }
      }
    } else this.sinceGrain = Number.MAX_VALUE;
    const out = this.ring[this.pos];
    this.ring[this.pos] = 0;
    this.pos = (this.pos + 1) % RING;
    return out;
  }
}

export class LlamaStream {
  constructor(rate, seed = 1) {
    this.rate = rate; this.seed = seed; this.s = { ...DEFAULTS };
    this.sample = 0; this.events = []; this.held = []; this.bendSemitones = 0;
    this.cur = { vowel: this.s.vowel, voice: this.s.voice, mouth: this.s.mouth };
    const grain = Math.max(16, Math.floor(rate * 0.02)), window = new Float64Array(grain), breath = new Float64Array(grain);
    const attack = Math.max(1, Math.floor(rate * 0.0018)), releaseStart = Math.floor(rate * 0.013), releaseLength = Math.max(1, Math.floor(rate * 0.007)), p1 = rate * 0.000202, p2 = rate * 0.000263;
    for (let i = 0; i < grain; i++) {
      window[i] = i < attack ? 0.5 * (1 - Math.cos(Math.PI * i / attack)) : i >= releaseStart ? 0.5 * (1 - Math.cos(Math.PI * (releaseLength + i - releaseStart) / releaseLength)) : 1;
      breath[i] = Math.sin(2 * Math.PI * i / p1) * Math.exp(-50 * Math.PI * 3 * i / rate) + Math.sin(2 * Math.PI * i / p2) * Math.exp(-50 * Math.PI * 3.5 * i / rate);
    }
    this.tables = { grain, window, breath, jitterPeriod: Math.max(1, Math.floor(rate * 0.104)) };
    this.tapL = Math.floor(rate * 0.3096); this.tapR = Math.floor(rate * 0.3984);
    this.echoL = new Float64Array(this.tapL); this.echoR = new Float64Array(this.tapR); this.echoPos = 0;
    this.leveler = 0; this.attackCoefficient = 1 - Math.exp(-1 / (rate * 0.01)); this.releaseCoefficient = 1 - Math.exp(-1 / (rate * 0.25));
    this.smooth = 1 - Math.exp(-1 / (rate * 0.012));
    this.build();
  }
  build() {
    const n = Math.min(7, Math.max(1, Math.round(this.s.unison)));
    this.voices = Array.from({ length: n }, (_, v) => {
      const spread = n === 1 ? 0 : (v / (n - 1)) * 2 - 1;
      return new Voice(this.rate, this.seed + v * 7919, spread * this.s.detuneCents, spread * Math.min(1, Math.max(0, this.s.spread)), this.tables);
    });
    // the voices sing what is held now, so changing the unison while singing does not silence it
    const top = this.held.at(-1);
    if (top) for (const v of this.voices) v.on(top.pitch + (top.fine || 0) / 100, top.velocity, false, this.s, this.sample);
  }
  /** New settings (any of the DEFAULTS keys). Vowel, voice and mouth glide to their new values; a change of the number of unison voices or their detune rebuilds the voices. */
  settings(next) {
    const rebuild = (next.unison !== undefined && Math.round(next.unison) !== this.voices.length) || (next.detuneCents !== undefined && next.detuneCents !== this.s.detuneCents) || (next.spread !== undefined && next.spread !== this.s.spread);
    this.s = { ...this.s, ...next };
    if (rebuild) this.build();
  }
  /** at: seconds on the clock of render() (0 or in the past: at once). */
  noteOn(pitch, velocity = 96, at = 0, fine = 0) { this.add({ at, type: 'on', pitch, velocity, fine }); }
  noteOff(pitch, at = 0) { this.add({ at, type: 'off', pitch }); }
  /** The pitch wheel: every voice is bent by this many semitones (it follows at once). */
  bend(semitones) { this.bendSemitones = Number(semitones) || 0; }
  allOff() { this.events = []; this.held = []; for (const v of this.voices) v.off(); }
  add(event) { this.events.push(event); this.events.sort((a, b) => a.at - b.at); }
  apply(event) {
    if (event.type === 'on') {
      const legato = this.held.length > 0;
      this.held.push({ pitch: event.pitch, velocity: event.velocity, fine: event.fine });
      for (const v of this.voices) v.on(event.pitch + (event.fine || 0) / 100, event.velocity, legato, this.s, this.sample);
    } else {
      const i = this.held.findIndex((h) => h.pitch === event.pitch);
      if (i < 0) return;
      this.held.splice(i, 1);
      const top = this.held.at(-1);
      for (const v of this.voices) { if (top) v.on(top.pitch + (top.fine || 0) / 100, top.velocity, true, this.s, this.sample); else v.off(); }
    }
  }
  /** Fills `count` samples of left and right. blockTime: the time, in seconds, of the first sample (the clock the events are given on). */
  render(left, right, count, blockTime = 0) {
    const s = this.s, rate = this.rate, gain = 10 ** (Math.min(12, Math.max(-48, s.gainDb)) / 20), level = Math.min(1, Math.max(0, s.leveler)), echo = Math.min(1, Math.max(0, s.echo));
    const share = Math.SQRT2 / Math.sqrt(this.voices.length);
    for (let i = 0; i < count; i++) {
      const time = blockTime + i / rate;
      while (this.events.length && this.events[0].at <= time) this.apply(this.events.shift());
      this.cur.vowel += (s.vowel - this.cur.vowel) * this.smooth; this.cur.voice += (s.voice - this.cur.voice) * this.smooth; this.cur.mouth += (s.mouth - this.cur.mouth) * this.smooth;
      let l = 0, r = 0;
      for (const v of this.voices) {
        const out = v.step(this.sample, s, this.cur, this.bendSemitones) * share;
        l += out * Math.cos((v.pan + 1) * Math.PI / 4); r += out * Math.sin((v.pan + 1) * Math.PI / 4);
      }
      if (echo > 0) {
        const dl = this.echoL[this.echoPos % this.tapL], dr = this.echoR[this.echoPos % this.tapR];
        this.echoL[this.echoPos % this.tapL] = l * echo + dl * 0.45; this.echoR[this.echoPos % this.tapR] = r * echo + dr * 0.45;
        l += dl; r += dr; this.echoPos++;
      }
      l *= gain; r *= gain;
      const peak = Math.max(Math.abs(l), Math.abs(r));
      this.leveler += (peak > this.leveler ? this.attackCoefficient : this.releaseCoefficient) * (peak - this.leveler);
      const g = 1 - level * (1 - (this.leveler > 0.3 ? 0.3 / this.leveler : 1));
      left[i] = soft(l * g); right[i] = soft(r * g);
      this.sample++;
    }
  }
}
