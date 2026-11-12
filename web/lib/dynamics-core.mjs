// The dynamics blocks (gate, limiter, de-esser) as streaming cores on shared envelope followers. The offline previews (clientside-gate/limiter/deesser.mjs) and the live
// AudioWorklet (dynamics-worklet.mjs) both run these classes, so the sound you hear while a knob moves is the sound of the render, sample for sample (the pattern of the
// Delay Llama). process(ins, outs, from, to) reads ins[channel][i] and writes outs[channel][i] for i in [from, to); all channels are linked (one detector).
import { cutoffAlpha } from './one-pole.mjs';

const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
/** A number from a parameter text such as "-50 dB" or "5 ms" (any unit words are ignored). */
export const readNumber = (value, fallback, unitPattern = /db|ms/gi) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(unitPattern, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};

// ---- the shared envelope followers ----
/** Peak follower: jumps up to a louder level at once and falls by a fraction of itself per sample (release / 4 samples to fall by e). */
export const followPeak = (envelope, level, releaseSamples) => (level > envelope ? level : envelope - (envelope / Math.max(1, releaseSamples / 4)));
/** Smoothing follower: a one-pole towards the level, with its own time for rising (attack) and falling (release), in samples. */
export const followSmooth = (envelope, level, attackSamples, releaseSamples) => envelope + (level - envelope) / (level > envelope ? attackSamples : releaseSamples);
/** Gain follower: goes down to the wanted gain at once, comes back up by at least 1/32768 or a part of the way to 1 per sample. */
export const followGain = (gain, wanted, releaseSamples) => (wanted < gain ? wanted : Math.min(wanted, gain + Math.max(1 / 32768, (1 - gain) / releaseSamples)));

/** AMPLE linked-channel gate: peak envelope, hold counter and an attack/release ramp of the gain. */
export class GateCore {
  constructor(sampleRate) { this.rate = sampleRate; this.gain = 0; this.envelope = 0; this.open = 0; this.configure({}); }

  configure(params) {
    const rate = this.rate;
    this.threshold = 10 ** (clamp(readNumber(params.threshold, -50), -80, 0) / 20);
    this.attack = Math.max(1, Math.trunc(clamp(readNumber(params.attack, 5), 1, 200) * rate / 1000));
    this.hold = Math.trunc(clamp(readNumber(params.hold, 80), 0, 1000) * rate / 1000);
    this.release = Math.max(1, Math.trunc(clamp(readNumber(params.release, 150), 5, 2000) * rate / 1000));
  }

  process(ins, outs, from, to) {
    let { gain, envelope, open } = this;
    for (let i = from; i < to; i++) {
      let level = 0;
      for (const channel of ins) level = Math.max(level, Math.abs(channel[i]));
      envelope = followPeak(envelope, level, this.release);
      if (envelope > this.threshold) open = this.hold;
      else if (open > 0) open--;
      const target = open > 0 || envelope > this.threshold ? 1 : 0;
      gain += target > gain ? Math.max(1 / 32768, (target - gain) / this.attack) : (target - gain) / this.release;
      gain = clamp(gain, 0, 1);
      for (let c = 0; c < ins.length; c++) outs[c][i] = ins[c][i] * gain;
    }
    this.gain = gain; this.envelope = envelope; this.open = open;
  }
}

/** AMPLE de-esser: attenuates the band above `frequency` with a linked detector. */
export class DeesserCore {
  constructor(sampleRate, channels = 2) { this.rate = sampleRate; this.low = new Array(channels).fill(0); this.envelope = 0; this.configure({}); }

  configure(params) {
    const rate = this.rate;
    const hz = clamp(Number.parseFloat(String(params.frequency ?? '6000').replace('Hz', '')) || 6000, 2000, rate / 2 - 100);
    this.coefficient = cutoffAlpha(hz, rate);   // as on the server (de-esser version 2)
    this.threshold = 10 ** (clamp(readNumber(params.threshold, -30, /dBFS|dB/gi), -60, 0) / 20);
    this.ratio = clamp(Number.parseFloat(params.ratio) || 4, 1, 20);
    this.attack = Math.max(1, Math.floor(rate / 2000)); this.release = Math.max(1, Math.floor(rate / 50));
  }

  process(ins, outs, from, to) {
    const low = this.low;
    while (low.length < ins.length) low.push(0);
    let envelope = this.envelope;
    for (let i = from; i < to; i++) {
      let level = 0;
      for (let c = 0; c < ins.length; c++) {
        low[c] += this.coefficient * (ins[c][i] - low[c]);
        level = Math.max(level, Math.abs(ins[c][i] - low[c]));
      }
      envelope = followSmooth(envelope, level, this.attack, this.release);
      const gain = envelope > this.threshold ? (this.threshold + (envelope - this.threshold) / this.ratio) / envelope : 1;
      for (let c = 0; c < ins.length; c++) outs[c][i] = low[c] + (ins[c][i] - low[c]) * gain;
    }
    this.envelope = envelope;
  }
}

/**
 * AMPLE linked-channel look-ahead limiter (float math approximates the Q15 gain path of the server). The sound comes out `latency` samples after it went in: the gain is the
 * running mean of the lowest wanted gain over the look-ahead window. A change of the look-ahead starts the window afresh.
 */
export class LimiterCore {
  constructor(sampleRate, channels = 2) { this.rate = sampleRate; this.channels = channels; this.params = null; this.configure({}); }

  configure(params) {
    const rate = this.rate;
    const ceilingDb = clamp(readNumber(params.ceiling, -1), -24, 0);
    this.ceiling = 10 ** (ceilingDb / 20) * 32767 / 32768;
    this.release = Math.max(1, Math.trunc(clamp(readNumber(params.release, 100), 10, 1000) * rate / 1000));
    const look = Math.max(1, Math.trunc(clamp(readNumber(params.lookahead, 5), 1, 20) * rate / 1000));
    if (look !== this.look) this.reset(look);
  }

  reset(look) {
    this.look = look; this.capacity = look + 2;
    this.queueIndex = new Int32Array(this.capacity); this.queueValue = new Float64Array(this.capacity); this.floorRing = new Float64Array(look + 1);
    this.delay = Array.from({ length: this.channels }, () => new Float64Array(look));
    this.head = 0; this.tail = 0; this.previous = 1; this.floorSum = 0; this.index = 0;
  }

  /** Samples between a sound going in and coming out. */
  get latency() { return this.look; }

  process(ins, outs, from, to) {
    const { look, capacity, queueIndex, queueValue, floorRing } = this, ceilingPcm = this.ceiling * 32768;
    while (this.delay.length < ins.length) this.delay.push(new Float64Array(look));
    let { head, tail, previous, floorSum } = this, i = this.index;
    for (let n = from; n < to; n++, i++) {
      let level = 0;
      for (const channel of ins) level = Math.max(level, Math.abs(channel[n] * 32768));
      const wanted = level > ceilingPcm ? ceilingPcm / level : 1;
      previous = followGain(previous, wanted, this.release);
      while (tail > head) {
        if (queueValue[(tail - 1) % capacity] < previous) break;
        tail--;
      }
      const slot = tail % capacity;
      queueIndex[slot] = i; queueValue[slot] = previous; tail++;
      while (head < tail && queueIndex[head % capacity] < i - look) head++;
      const floor = queueValue[head % capacity];
      floorSum += floor;
      const ring = i % floorRing.length, old = i >= look ? floorRing[(i - look) % floorRing.length] : 0;
      if (i >= look) floorSum -= old;
      const gain = floorSum / look, slotIn = i % look;
      for (let c = 0; c < ins.length; c++) {
        const delayed = this.delay[c][slotIn];   // the sample that went in `look` samples ago (silence while the window fills)
        outs[c][n] = i >= look ? delayed * gain : 0;
        this.delay[c][slotIn] = ins[c][n];
      }
      floorRing[ring] = floor;
    }
    this.head = head; this.tail = tail; this.previous = previous; this.floorSum = floorSum; this.index = i;
  }
}

/**
 * AMPLE linked-channel compressor with an optional sidechain key. The detector reads the key when there is one (high-passed above `keyFilter` Hz, as on the server, compressor
 * version 6) and the program itself otherwise; the gain applies to the program. process(ins, outs, from, to, keys): keys is an array of channels or null.
 */
export class CompressorCore {
  constructor(sampleRate) { this.rate = sampleRate; this.keyRate = sampleRate; this.envelope = 0; this.low = []; this.configure({}); }

  configure(params) {
    const rate = this.rate;
    this.threshold = 10 ** (clamp(readNumber(params.threshold, -18, /db|ms|hz/gi), -60, 0) / 20);
    this.ratio = clamp(readNumber(params.ratio, 4, /db|ms|hz/gi), 1, 20);
    this.attack = Math.max(1, Math.trunc(clamp(readNumber(params.attack, 10, /db|ms|hz/gi), 1, 500) * rate / 1000));
    this.release = Math.max(1, Math.trunc(clamp(readNumber(params.release, 120, /db|ms|hz/gi), 5, 2000) * rate / 1000));
    this.makeup = 10 ** (clamp(readNumber(params.makeup, 0, /db|ms|hz/gi), 0, 12) / 20);
    this.keyHz = clamp(readNumber(params.keyFilter, 0, /db|ms|hz/gi), 0, 8000);
    this.alpha = cutoffAlpha(this.keyHz, this.keyRate);
  }

  /** The sample rate of the key when it differs from the program (the filter then follows the key's own rate, as the offline render always did). */
  setKeyRate(rate) { this.keyRate = rate; this.alpha = cutoffAlpha(this.keyHz, rate); }

  process(ins, outs, from, to, keys = null) {
    let envelope = this.envelope;
    const low = this.low, filtered = keys && this.keyHz > 0;
    while (keys && low.length < keys.length) low.push(0);
    for (let i = from; i < to; i++) {
      let level = 0;
      if (keys) {
        for (let c = 0; c < keys.length; c++) {
          const key = keys[c];
          if (i >= key.length) continue;
          let value = key[i];
          if (filtered) { low[c] += this.alpha * (value - low[c]); value = Math.fround(value - Math.fround(low[c])); }   // the offline filter stored float32 samples
          level = Math.max(level, Math.abs(value));
        }
      } else for (const channel of ins) level = Math.max(level, Math.abs(channel[i]));
      envelope += (level - envelope) / (level > envelope ? this.attack : this.release);
      const gain = envelope > this.threshold ? (this.threshold + (envelope - this.threshold) / this.ratio) / envelope : 1;
      for (let c = 0; c < ins.length; c++) outs[c][i] = ins[c][i] * gain * this.makeup;
    }
    this.envelope = envelope;
  }
}

/** Builds the core of a block type, or null. */
export function dynamicsCore(kind, sampleRate, channels = 2) {
  if (kind === 'gate') return new GateCore(sampleRate);
  if (kind === 'deesser') return new DeesserCore(sampleRate, channels);
  if (kind === 'limiter') return new LimiterCore(sampleRate, channels);
  if (kind === 'compressor') return new CompressorCore(sampleRate);
  return null;
}
