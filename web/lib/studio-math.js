/** How many filter passes a slope in dB per octave means (12 is 1, 24 is 2, 36 is 3, 48 is 4), like ResonantFilter.PassesOf on the server. */
export function filterPasses(slope) { return { 24: 2, 36: 3, 48: 4 }[String(slope ?? '12').trim()] || 1; }

export function clamp(value, min, max) { return Math.max(min, Math.min(max, value)); }

export function normalize(value, min, max, scale) {
  if (!(max > min)) return 0;
  const bounded = clamp(value, min, max);
  if (scale === 'log' && min > 0) return Math.log(bounded / min) / Math.log(max / min);
  return (bounded - min) / (max - min);
}

export function denormalize(position, min, max, scale) {
  const p = clamp(position, 0, 1);
  if (scale === 'log' && min > 0) return min * ((max / min) ** p);
  return min + p * (max - min);
}

export function knobDragValue(startValue, deltaPixels, min, max, fine = false) {
  const sensitivity = (max - min) / 200 * (fine ? 0.1 : 1);
  return clamp(startValue + deltaPixels * sensitivity, min, max);
}

export function snap(value, step, min, max) {
  if (!(step > 0)) return clamp(value, min, max);
  return clamp(min + Math.round((value - min) / step) * step, min, max);
}

export function formatValue(value, decimals, unit) {
  const rounded = Number(value).toFixed(decimals).replace(/\.0+$|(?<=\.[0-9]*)0+$/g, '').replace(/\.$/, '');
  return unit ? `${rounded} ${unit}` : rounded;
}

export function interpolateCurve(points, x) {
  if (!points.length) return 0;
  const ordered = [...points].sort((a, b) => a.x - b.x);
  if (x <= ordered[0].x) return ordered[0].y;
  for (let i = 1; i < ordered.length; i++) {
    const right = ordered[i]; const left = ordered[i - 1];
    if (x <= right.x) { const t = (x - left.x) / (right.x - left.x); return left.y + (right.y - left.y) * t; }
  }
  return ordered.at(-1).y;
}

export function compressorOutput(inputDb, thresholdDb, ratio) {
  if (inputDb <= thresholdDb) return inputDb;
  const threshold = 10 ** (thresholdDb / 20), input = 10 ** (inputDb / 20);
  return 20 * Math.log10(threshold + (input - threshold) / Math.max(1, ratio));
}

export function biquadMagnitude(type, frequency, cutoff, q = Math.SQRT1_2, gainDb = 0, sampleRate = 48000) {
  const w = 2 * Math.PI * Math.min(frequency, sampleRate / 2) / sampleRate;
  const cw = Math.cos(w), sw = Math.sin(w), a = sw / (2 * Math.max(0.001, q));
  const w0 = 2 * Math.PI * clamp(cutoff, 1, sampleRate / 2) / sampleRate;
  const c0 = Math.cos(w0), s0 = Math.sin(w0);
  let b0, b1, b2, a0, a1, a2;
  if (type === 'lowpass') { b0 = (1 - c0) / 2; b1 = 1 - c0; b2 = b0; a0 = 1 + a; a1 = -2 * c0; a2 = 1 - a; }
  else if (type === 'bandpass') { b0 = a; b1 = 0; b2 = -a; a0 = 1 + a; a1 = -2 * c0; a2 = 1 - a; }
  else if (type === 'notch') { b0 = 1; b1 = -2 * c0; b2 = 1; a0 = 1 + a; a1 = -2 * c0; a2 = 1 - a; }
  else if (type === 'highpass') { b0 = (1 + c0) / 2; b1 = -(1 + c0); b2 = b0; a0 = 1 + a; a1 = -2 * c0; a2 = 1 - a; }
  else {
    const A = 10 ** (gainDb / 40), alpha = s0 / (2 * Math.max(0.001, q));
    if (type === 'peaking') { b0 = 1 + alpha * A; b1 = -2 * c0; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * c0; a2 = 1 - alpha / A; }
    else {
      const beta = 2 * Math.sqrt(A) * alpha;
      if (type === 'lowshelf') { b0 = A * ((A + 1) - (A - 1) * c0 + beta); b1 = 2 * A * ((A - 1) - (A + 1) * c0); b2 = A * ((A + 1) - (A - 1) * c0 - beta); a0 = (A + 1) + (A - 1) * c0 + beta; a1 = -2 * ((A - 1) + (A + 1) * c0); a2 = (A + 1) + (A - 1) * c0 - beta; }
      else { b0 = A * ((A + 1) + (A - 1) * c0 + beta); b1 = -2 * A * ((A - 1) + (A + 1) * c0); b2 = A * ((A + 1) + (A - 1) * c0 - beta); a0 = (A + 1) - (A - 1) * c0 + beta; a1 = 2 * ((A - 1) - (A + 1) * c0); a2 = (A + 1) - (A - 1) * c0 - beta; }
    }
  }
  const nr = b0 + b1 * cw + b2 * Math.cos(2 * w), ni = -b1 * sw - b2 * Math.sin(2 * w);
  const dr = a0 + a1 * cw + a2 * Math.cos(2 * w), di = -a1 * sw - a2 * Math.sin(2 * w);
  const magnitude = Math.hypot(nr, ni) / Math.max(1e-12, Math.hypot(dr, di));
  return 20 * Math.log10(Math.max(1e-12, magnitude));
}

/** A number from a parameter text such as "-12 dB" or "4760 Hz" (a plain number also works); the fallback when there is none. */
const amount = (value, fallback) => { const parsed = Number.parseFloat(String(value ?? '').replace(/[a-z%\s]+$/i, '')); return Number.isFinite(parsed) ? parsed : fallback; };

export function equalizerResponse(frequency, values, sampleRate = 48000) {
  const lowHz = amount(values.low_hz, 250) || 250, highHz = amount(values.high_hz, 3000) || 3000;
  const midHz = Math.sqrt(lowHz * highHz);
  return biquadMagnitude('lowshelf', frequency, lowHz, Math.SQRT1_2, amount(values.low, 0), sampleRate)
    + biquadMagnitude('peaking', frequency, midHz, 0.8, amount(values.mid, 0), sampleRate)
    + biquadMagnitude('highshelf', frequency, highHz, Math.SQRT1_2, amount(values.high, 0), sampleRate);
}

export function spectrumBins(samples, sampleRate, count) {
  const length = Math.min(samples.length, 2048);
  if (!length || !(sampleRate > 0) || !(count > 0)) return [];
  const windowed = new Float64Array(length);
  let windowGain = 0;
  for (let index = 0; index < length; index++) {
    const window = length === 1 ? 1 : 0.5 - 0.5 * Math.cos(2 * Math.PI * index / (length - 1));
    windowed[index] = samples[index] * window; windowGain += window;
  }
  return Array.from({ length: count }, (_, index) => {
    const frequency = 20 * (1000 ** (index / Math.max(1, count - 1))), omega = 2 * Math.PI * frequency / sampleRate;
    const coefficient = 2 * Math.cos(omega); let previous = 0, beforePrevious = 0;
    for (const sample of windowed) { const current = sample + coefficient * previous - beforePrevious; beforePrevious = previous; previous = current; }
    const power = Math.max(0, previous * previous + beforePrevious * beforePrevious - coefficient * previous * beforePrevious);
    const db = 20 * Math.log10(Math.max(1e-6, Math.sqrt(power) * 2 / Math.max(1, windowGain)));
    return { frequency, db: clamp(db, -72, 6) };
  });
}

export function duckGain(step, pattern, depthDb, releaseSteps) {
  const active = [...pattern].lastIndexOf('x', Math.max(0, Math.min(15, Math.floor(step))));
  if (active < 0) return 1;
  const elapsed = step - active;
  if (elapsed < 0 || elapsed > releaseSteps) return 1;
  const minimum = 10 ** (-clamp(depthDb, 0, 40) / 20);
  return minimum + (1 - minimum) * clamp(elapsed / Math.max(0.001, releaseSteps), 0, 1);
}

export function distortionSample(input, driveDb, mode) {
  const driven = clamp(input * (10 ** (clamp(driveDb, 0, 36) / 20)), -8, 8);
  return mode === 'hard' ? clamp(driven, -1, 1) : Math.tanh(driven);
}

export function delayTapGain(feedbackPercent, tap) { return (clamp(feedbackPercent, 0, 90) / 100) ** Math.max(0, tap); }

export function estimatedRt60(sizePercent, decayPercent) {
  return Math.max(0.1, 0.2 + clamp(sizePercent, 1, 100) / 100 * 2.8) * (0.2 + clamp(decayPercent, 0, 95) / 95 * 2.8);
}

export const PEQ_BANDS = [1, 2, 3, 4, 5, 6];
const PEQ_BIQUAD = { peak: 'peaking', lowshelf: 'lowshelf', highshelf: 'highshelf', notch: 'notch', lowcut: 'highpass', highcut: 'lowpass' };
const PEQ_DEFAULTS = { 1: ['lowshelf', 80], 2: ['peak', 250], 3: ['peak', 800], 4: ['peak', 2500], 5: ['peak', 6000], 6: ['highshelf', 12000] };
const peqNumber = (params, name, fallback) => { const value = Number.parseFloat(String(params[name] ?? '').replace(/[a-z ]/gi, '')); return Number.isFinite(value) ? value : fallback; };

/** The state of one band from the parameters (a missing one has the default of the template). */
export function bandOf(params, index) {
  const [type, hz] = PEQ_DEFAULTS[index];
  return { enabled: String(params[`band${index}_enabled`] ?? 'on').trim() !== 'off', type: String(params[`band${index}_type`] ?? type), hz: peqNumber(params, `band${index}_hz`, hz), gain: peqNumber(params, `band${index}_gain`, 0), q: peqNumber(params, `band${index}_q`, 1) };
}

/** The response in dB of the six bands together at a frequency. */
export function peqResponse(frequency, params) {
  let db = 0;
  for (const index of PEQ_BANDS) {
    const b = bandOf(params, index);
    if (!b.enabled) continue;
    db += biquadMagnitude(PEQ_BIQUAD[b.type] ?? 'peaking', frequency, b.hz, Math.max(0.1, b.q), b.gain);
  }
  return db;
}

