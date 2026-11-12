// The follow and fire blocks in the browser (port of FollowRenderer and FireRenderer in ControlBlocks.cs): follow turns the loudness of a band of a sound into a control (per mille, 1000 values a
// second) and a gate; fire turns a control that crosses a level into notes. Float math instead of the server's integer fixed point; `auto` settings are not analysed here (they return null).
import { fixed } from './control-live.mjs';

const unq = (t) => String(t ?? '').replace(/^"|"$/g, '').trim();
const msOf = (t, fb) => { const m = /^(-?\d+(?:\.\d+)?)\s*(ms|s)?/i.exec(unq(t)); if (!m) return fb; return Math.round(Number(m[1]) * (m[2]?.toLowerCase() === 's' ? 1000 : 1)); };
const lowPass = (x, rate, hz) => { const a = Math.min(1, Math.max(1 / 65536, 6.283185 * hz / (rate + 6.283185 * hz))), y = new Float32Array(x.length); let s = 0; for (let i = 0; i < x.length; i++) { y[i] = s; s += (x[i] - s) * a; } return y; };

/** The band of a mono signal: 'full', 'low', 'mid', 'high' or 'lo..hi' (Hz), as the server. */
export function bandOf(x, rate, band) {
  const b = unq(band).toLowerCase();
  if (b === 'low') return lowPass(x, rate, 200);
  if (b === 'mid') { const a = lowPass(x, rate, 2000), c = lowPass(x, rate, 200); return a.map((v, i) => v - c[i]); }
  if (b === 'high') { const a = lowPass(x, rate, 2000); return x.map((v, i) => v - a[i]); }
  const m = /^(\d+(?:\.\d+)?)\s*(?:hz)?\s*(?:\.\.|-|to)\s*(\d+(?:\.\d+)?)\s*(?:hz)?$/.exec(b);
  if (m) {
    const clip = (v) => Math.min(rate / 2 - 100, Math.max(10, Math.round(v))), lo = clip(Number(m[1])), hi = clip(Number(m[2]));
    if (hi > lo) { const top = lowPass(x, rate, hi), bottom = lowPass(x, rate, lo); return top.map((v, i) => v - bottom[i]); }
  }
  return x;
}

/** { values (the control, in the `range`), gate (0 or 1000) } at 1000 a second for an audio { sampleRate, channels }; null when a setting is `auto`. */
export function followSeries(audio, params) {
  if ([params.band, params.attack, params.release, params.baseline_window].some((v) => unq(v).toLowerCase() === 'auto')) return null;
  const rate = audio.sampleRate, n = audio.channels[0].length, mono = new Float32Array(n);
  for (let i = 0; i < n; i++) mono[i] = audio.channels.reduce((s, c) => s + c[i], 0) / audio.channels.length;
  const count = Math.floor(n * 1000 / rate) + 1;
  if (params.enabled === 'off') return { values: new Float32Array(count), gate: new Float32Array(count) };
  const band = bandOf(mono, rate, params.band || 'full'), attackMs = msOf(params.attack, 80), releaseMs = msOf(params.release, 600), windowMs = Math.max(1, msOf(params.baseline_window, 10000));
  const attack = Math.min(1, 1 / (attackMs * rate / 1000 + 1)), release = Math.min(1, 1 / (releaseMs * rate / 1000 + 1));
  const series = new Float64Array(count);
  let envelope = 0, next = 0;
  for (let i = 0; i < n && next < count; i++) {
    const v = Math.abs(band[i]);
    envelope += (v - envelope) * (v > envelope ? attack : release);
    while (next < count && Math.floor(next * rate / 1000) <= i) series[next++] = envelope;
  }
  while (next < count) series[next++] = envelope;
  let level = series;
  if (unq(params.baseline) !== 'off') {
    const slow = 1 / (windowMs + 1); let average = series[0]; level = new Float64Array(count);
    for (let k = 0; k < count; k++) { level[k] = Math.max(0, series[k] - average); average += (series[k] - average) * slow; }
  }
  const reference = unq(params.normalize) === 'off' ? 1 : Math.max(1e-12, level.reduce((m, v) => Math.max(m, v), 0));
  const soft = unq(params.curve) === 'soft', [lowText, highText] = unq(params.range || '0 .. 1').split('..').map((t) => t.trim());
  const low = lowText === undefined ? 0 : fixed(lowText, 0), high = highText === undefined ? 1000 : fixed(highText, 1000);
  const values = new Float32Array(count), fractions = new Float32Array(count);
  for (let k = 0; k < count; k++) {
    let f = Math.min(1000, Math.floor(level[k] * 1000 / reference));
    if (soft) f = Math.floor(Math.sqrt(f * 1000));
    fractions[k] = f; values[k] = low + Math.floor(f * (high - low) / 1000);
  }
  const threshold = Math.min(1000, Math.max(0, fixed(params.threshold, 300))), hold = msOf(params.hold, 500), gate = new Float32Array(count);
  let lastAbove = -Infinity;
  for (let k = 0; k < count; k++) { if (fractions[k] >= threshold) lastAbove = k; gate[k] = k - lastAbove <= hold ? 1000 : 0; }
  return { values, gate };
}

/** Notes from a control (per mille at 1000 a second, the first value at bar `firstBar`): [{ bar, beat, beats, pitch, velocity, pan }] like the server's fire. */
export function fireNotes(values, params, { samplesPerBar, rate, firstBar = 1, bars }) {
  if (params.enabled === 'off') return [];
  const threshold = fixed(params.threshold, 500), rising = unq(params.direction) !== 'falling', pitch = Math.min(127, Math.max(0, Math.trunc(fixed(params.note, 60000) / 1000)));
  const velocityText = unq(params.velocity || '100'), fixedVelocity = Math.min(127, Math.max(1, Math.trunc(fixed(velocityText, 100000) / 1000)));
  const gap = msOf(params.gap, 600), lengthMs = Math.max(10, msOf(params.length, 250)), first = (firstBar - 1) * samplesPerBar;
  const beats = Math.trunc(Math.trunc(lengthMs * rate / 1000) * 4000 / samplesPerBar) / 1000, out = [];
  let last = -Infinity;
  for (let k = 1; k < values.length; k++) {
    const crossed = rising ? values[k - 1] < threshold && values[k] >= threshold : values[k - 1] > threshold && values[k] <= threshold;
    if (!crossed || k - last < gap) continue;
    last = k;
    const t = first + Math.trunc(k * rate / 1000), bar = Math.trunc(t / samplesPerBar) + 1;
    if (bar < firstBar || bar >= firstBar + bars) continue;
    out.push({ bar, beat: Math.trunc((t % samplesPerBar) * 4000 / samplesPerBar) / 1000, beats, pitch, velocity: velocityText.toLowerCase() === 'level' ? Math.min(127, Math.max(1, Math.trunc(1 + values[k] * 126 / 1000))) : fixedVelocity, pan: 0 });
  }
  return out;
}
