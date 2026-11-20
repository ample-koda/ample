// Controls and automated mixes in the client preview, with the integer arithmetic of the server so the preview follows the build:
//  - a control (ControlCurve.cs): 1000 values a second from its first bar, read at a sample with a truncating straight line;
//  - curve, map and follow (ControlBlocks.cs; the `auto` settings of follow are chosen from the recording as FollowAnalysis.Suggest);
//  - a control connected to a parameter becomes points every millisecond, scaled to the range of the parameter (ControlBinding.Resolve);
//  - points joined per sample (Automation.CurveAt) and the dry/wet blend of an automated mix (Effects.cs Blend).
// Pure, tested in node (web/lib/test/clientside-control-curves.test.mjs).
import { panPoints } from './live-pan-auto.mjs';
import { volumeCurve } from './volume-auto.mjs';
import { expressionToInt } from './control-expression.mjs';

export const CONTROL_RATE = 1000;
const unquote = (text) => String(text ?? '').trim().replace(/^"|"$/g, '');
const idiv = (a, b) => Math.trunc(a / b);   // C# long division of whole numbers (all values here stay far below 2^53)

/** A number with a unit word as fixed point with three decimals (ControlBlocks.Fixed): "0.5" is 500, "30 %" is 300. */
export function fixed(text, fallback) {
  const m = /^(-?)(\d+)(?:\.(\d+))?\s*([A-Za-z%]*)/.exec(unquote(text));
  if (!m) return fallback;
  let value = Number(m[2]) * 1000 + (m[3] ? Number((m[3] + '000').slice(0, 3)) : 0);
  if (m[4] === '%') value = idiv(value, 100);
  return m[1] === '-' ? -value : value;
}

/** A time in milliseconds (ControlBlocks.Milliseconds): "80 ms", "1.5 s". */
export function milliseconds(text, fallback) {
  const m = /^(-?)(\d+)(?:\.(\d+))?\s*([A-Za-z%]*)/.exec(unquote(text));
  if (!m) return fallback;
  const value = fixed(text, fallback * 1000);
  return m[4].toLowerCase() === 's' ? value : idiv(value + 500, 1000);
}

/** The unit of a curve, map or follow block (ControlTypes.UnitOfBlock): null for a general control. */
export const unitOf = (params) => { const u = unquote(params?.unit); return !u || u === 'none' ? null : u; };

/** The value of a control at a sample of the song (ControlCurve.At). */
export function controlAt(control, absoluteSample, sampleRate, samplesPerBar) {
  const v = control.values;
  if (!v.length) return 0;
  const rel = absoluteSample - (control.firstBar - 1) * samplesPerBar;
  if (rel <= 0) return v[0];
  const scaled = rel * CONTROL_RATE, index = Math.floor(scaled / sampleRate);
  if (index >= v.length - 1) return v[v.length - 1];
  const frac = scaled - index * sampleRate, a = v[index], b = v[index + 1];
  return a + idiv((b - a) * frac, sampleRate);
}

/** Points joined by straight lines per sample (Automation.CurveAt): points [{sample, value}] on the song, the take from bar `firstBar`. */
export function curveAt(points, firstBar, samplesPerBar, length, valueOf = (v) => v) {
  const values = points.map((p) => valueOf(p.value)), out = new Int32Array(length);
  let segment = 0;
  for (let i = 0; i < length; i++) {
    const absolute = (firstBar - 1) * samplesPerBar + i;
    while (segment + 1 < points.length && absolute >= points[segment + 1].sample) segment++;
    if (absolute < points[0].sample || segment + 1 >= points.length) { out[i] = absolute < points[0].sample ? values[0] : values[values.length - 1]; continue; }
    const from = points[segment].sample, to = points[segment + 1].sample;
    out[i] = values[segment] + idiv((values[segment + 1] - values[segment]) * (absolute - from), Math.max(1, to - from));
  }
  return out;
}

/** The curve block (CurveRenderer): a plain number all the time, or auto/lfo/env as per mille (a typed control holds its unit times 1000). */
export function curveControl(params, firstBar, barCount, samplesPerBar, rate) {
  const length = barCount * samplesPerBar, count = Math.floor(length * CONTROL_RATE / rate) + 1, typed = unitOf(params) !== null;
  const values = new Int32Array(count), text = unquote(params?.value);
  const points = params?.enabled !== 'off' ? panPoints(text, samplesPerBar, rate) : null;
  if (points) {
    const perSample = curveAt(points, firstBar, samplesPerBar, length);
    for (let k = 0; k < count; k++) {
      const at = Math.min(perSample.length - 1, Math.floor(k * rate / CONTROL_RATE));
      values[k] = perSample.length === 0 ? 0 : typed ? perSample[at] * 1000 : perSample[at];
    }
  } else if (/^\s*(auto|lfo|env)\(/.test(text) && params?.enabled !== 'off') return null;   // a curve text this port does not read: the server makes it
  else values.fill(params?.enabled === 'off' && /^\s*(auto|lfo|env)\(/.test(text) ? 0 : fixed(text, 0));
  return { firstBar, unit: unitOf(params), values };
}

/** The map block (MapRenderer): rescale, invert, limit and smooth a control. */
export function mapControl(params, source) {
  const inLow = fixed(params?.in_low, 0), inHigh = fixed(params?.in_high, 1000);
  let outLow = fixed(params?.out_low, 0), outHigh = fixed(params?.out_high, 1000);
  const invert = params?.invert === 'on', limit = params?.limit !== 'off', smooth = milliseconds(params?.smooth, 0);
  if (invert) [outLow, outHigh] = [outHigh, outLow];
  const span = inHigh - inLow === 0 ? 1 : inHigh - inLow, lo = Math.min(outLow, outHigh), hi = Math.max(outLow, outHigh);
  const a = smooth <= 0 ? 65536 : Math.max(1, Math.floor(65536 / (smooth + 1)));
  const values = new Int32Array(source.values.length);
  let state = 0;
  for (let k = 0; k < values.length; k++) {
    let mapped = outLow + idiv((source.values[k] - inLow) * (outHigh - outLow), span);
    if (limit) mapped = Math.max(lo, Math.min(hi, mapped));
    state = k === 0 || smooth <= 0 ? mapped : state + Math.floor((mapped - state) * a / 65536);   // C# >> 16 rounds down
    values[k] = state;
  }
  return { firstBar: source.firstBar, unit: unitOf(params), values };
}

const lowPass = (x, rate, hz) => {
  // ControlBlocks.LowPass: a one-pole low-pass with a 16-bit integer coefficient, 8 extra bits in the state
  let a = Math.floor(6283185 * hz * 65536 / (1000000 * rate + 6283185 * hz));
  a = Math.max(1, Math.min(65536, a));
  const y = new Int32Array(x.length);
  let state = 0;
  for (let i = 0; i < x.length; i++) { y[i] = Math.floor(state / 256); state += Math.floor((x[i] * 256 - state) * a / 65536); }
  return y;
};

function band(x, rate, text) {
  const b = unquote(text).toLowerCase(), hz = (v) => Math.max(10, Math.min(Math.floor(rate / 2) - 100, Math.round(v)));
  if (b === 'low') return lowPass(x, rate, 200);
  if (b === 'mid') { const a = lowPass(x, rate, 2000), c = lowPass(x, rate, 200); return a.map((v, i) => v - c[i]); }
  if (b === 'high') { const a = lowPass(x, rate, 2000); return x.map((v, i) => v - a[i]); }
  const m = /^(\d+(?:\.\d+)?)\s*(?:hz)?\s*(?:\.\.|-|to)\s*(\d+(?:\.\d+)?)\s*(?:hz)?$/.exec(b);
  if (m) { const lo = hz(Number(m[1])), hi = hz(Number(m[2])); if (hi > lo) { const top = lowPass(x, rate, hi), bottom = lowPass(x, rate, lo); return top.map((v, i) => v - bottom[i]); } }
  return x;
}

const isqrt = (value) => { if (value <= 0) return 0; let x = Math.floor(Math.sqrt(value)); while (x * x > value) x--; while ((x + 1) * (x + 1) <= value) x++; return x; };

/** The level of a band as one value per millisecond (ControlBlocks.Envelope): a rising and a falling one-pole envelope of the absolute value, in 8 extra bits. */
function envelopeSeries(signal, rate, attackMs, releaseMs, count) {
  const attack = Math.max(1, Math.floor(65536 / (Math.floor(attackMs * rate / 1000) + 1))), release = Math.max(1, Math.floor(65536 / (Math.floor(releaseMs * rate / 1000) + 1)));
  const series = new Array(count).fill(0);
  let envelope = 0, next = 0;
  for (let i = 0; i < signal.length && next < count; i++) {
    const v = Math.abs(signal[i]) * 256;
    envelope += Math.floor((v - envelope) * (v > envelope ? attack : release) / 65536);
    while (next < count && Math.floor(next * rate / CONTROL_RATE) <= i) series[next++] = envelope;
  }
  while (next < count) series[next++] = envelope;
  return series;
}

/** The middle and the loud end (95th percentile) of a series, as FollowAnalysis reads them. */
function spreadOf(series) {
  const sorted = Float64Array.from(series).sort();
  return { median: sorted[Math.floor(sorted.length / 2)], top: sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 95 / 100))] };
}

/** How much the loud end of a series stands out from its middle, per mille of the loud end (FollowAnalysis.Prominence). */
function prominence(series) {
  if (!series.length) return 0;
  const { median, top } = spreadOf(series);
  return top <= 0 ? 0 : Math.max(0, Math.min(1000, idiv((top - median) * 1000, top)));
}

/** The median length in milliseconds of the stretches above the middle of the background and the loud end (FollowAnalysis.MedianExcursion). */
function medianExcursion(series) {
  const { median, top } = spreadOf(series), level = median + idiv(top - median, 2), lengths = [];
  let run = 0;
  for (const v of series) {
    if (v > level && top > median) run++;
    else if (run > 0) { lengths.push(run); run = 0; }
  }
  if (run > 0) lengths.push(run);
  if (!lengths.length) return 1000;
  lengths.sort((a, b) => a - b);
  return lengths[Math.floor(lengths.length / 2)];
}

/**
 * The settings of a follow block that are `auto`, chosen from the recording (FollowAnalysis.Suggest): the band (low, mid or high) whose loud moments stand out most from their own
 * background, and the times from how long such a moment lasts.
 * @returns {{ band: string, baselineSeconds: number, attackMilliseconds: number, releaseMilliseconds: number }}
 */
export function suggestFollow(mono, rate) {
  const count = Math.floor(mono.length * CONTROL_RATE / rate) + 1;
  let best = 'low', bestScore = -1, bestSeries = null;
  for (const name of ['low', 'mid', 'high']) {
    const series = envelopeSeries(band(mono, rate, name), rate, 30, 250, count), score = prominence(series);
    if (score > bestScore) { best = name; bestScore = score; bestSeries = series; }
  }
  const duration = bestSeries ? medianExcursion(bestSeries) : 1000, clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
  return { band: best, baselineSeconds: clamp(idiv(duration * 5, 1000) + 1, 3, 30), attackMilliseconds: clamp(idiv(duration, 8), 20, 300), releaseMilliseconds: clamp(idiv(duration, 3), 150, 2000) };
}

/**
 * The follow block (FollowRenderer) on a mono sound of whole numbers: { level, gate } controls. A setting that is `auto` (band, attack, release, baseline_window) comes from
 * the recording (suggestFollow), as on the server.
 */
export function followControl(params, mono, rate, firstBar) {
  const text = (name) => unquote(params?.[name]), isAuto = (name) => text(name).toLowerCase() === 'auto';
  const count = Math.floor(mono.length * CONTROL_RATE / rate) + 1, unit = unitOf(params);
  if (text('enabled') === 'off') return { level: { firstBar, unit, values: new Int32Array(count) }, gate: { firstBar, unit: null, values: new Int32Array(count) } };
  const suggestion = ['band', 'attack', 'release', 'baseline_window'].some(isAuto) ? suggestFollow(mono, rate) : null;
  const attackMs = isAuto('attack') ? suggestion.attackMilliseconds : milliseconds(text('attack') || '80 ms', 80);
  const releaseMs = isAuto('release') ? suggestion.releaseMilliseconds : milliseconds(text('release') || '600 ms', 600);
  const windowMs = isAuto('baseline_window') ? suggestion.baselineSeconds * 1000 : Math.max(1, milliseconds(text('baseline_window') || '10 s', 10000));
  const signal = band(mono, rate, isAuto('band') ? suggestion.band : text('band') || 'full');
  const series = envelopeSeries(signal, rate, attackMs, releaseMs, count);
  let level = series;
  if (text('baseline') !== 'off') {
    const slow = Math.max(1, Math.floor(65536 / (windowMs + 1)));
    let average = series.length ? series[0] : 0;
    level = new Array(count);
    for (let k = 0; k < count; k++) { level[k] = Math.max(0, series[k] - average); average += Math.floor((series[k] - average) * slow / 65536); }
  }
  let loudest = 1; for (const v of level) if (v > loudest) loudest = v;
  const reference = text('normalize') === 'off' ? 32768 * 256 : loudest;
  const soft = text('curve') === 'soft';
  const parts = text('range').split('..').map((p) => p.trim());
  const low = parts.length === 2 ? fixed(parts[0], 0) : 0, high = parts.length === 2 ? fixed(parts[1], 1000) : 1000;
  const values = new Int32Array(count), fractions = new Int32Array(count);
  for (let k = 0; k < count; k++) {
    let f = Math.min(1000, Math.floor(level[k] * 1000 / reference));
    if (soft) f = isqrt(f * 1000);
    fractions[k] = f;
    values[k] = low + idiv(f * (high - low), 1000);
  }
  const threshold = Math.max(0, Math.min(1000, fixed(text('threshold') || '0.3', 300))), hold = milliseconds(text('hold') || '500 ms', 500);
  const gate = new Int32Array(count);
  let lastAbove = null;
  for (let k = 0; k < count; k++) { if (fractions[k] >= threshold) lastAbove = k; gate[k] = lastAbove !== null && k - lastAbove <= hold ? 1000 : 0; }
  return { level: { firstBar, unit, values }, gate: { firstBar, unit: null, values: gate } };
}

/**
 * The points of a parameter that a control is connected to (ControlBinding.Resolve, the connected case): one every millisecond from the first bar of the
 * render to its end; a general control (0..1000) is scaled to the range of the parameter, a typed one is the value itself.
 */
export function connectedPoints(control, range, firstBar, barCount, samplesPerBar, rate) {
  const start = (firstBar - 1) * samplesPerBar, end = start + barCount * samplesPerBar;
  const min = Math.round(range?.min ?? 0), max = Math.round(range?.max ?? 1000), points = [];
  for (let ms = 0; ; ms++) {
    const t = start + Math.floor(ms * rate / CONTROL_RATE), value = controlAt(control, t, rate, samplesPerBar);
    points.push({ sample: t, value: control.unit === null ? min + idiv((max - min) * Math.max(0, Math.min(1000, value)) + 500, 1000) : idiv(value, 1000) });
    if (t >= end) break;
  }
  return points;
}

/**
 * The points of a parameter that is an expression over controls (ControlBinding.Resolve, the expression case): one every millisecond from the first bar of the render to its
 * end, each the expression worked out in fixed point and rounded to a whole number. A reference that is not a control (null) reads 0.
 * @param {{ evaluate: (valueOf: (index: number) => number) => number }} expression from parseExpression
 * @param {(object | null)[]} controls the control of every reference, in the order of the references
 */
export function expressionPoints(expression, controls, firstBar, barCount, samplesPerBar, rate) {
  const start = (firstBar - 1) * samplesPerBar, end = start + barCount * samplesPerBar, points = [];
  for (let ms = 0; ; ms++) {
    const t = start + Math.floor(ms * rate / CONTROL_RATE);
    points.push({ sample: t, value: expressionToInt(expression.evaluate((i) => (controls[i] ? controlAt(controls[i], t, rate, samplesPerBar) : 0))) });
    if (t >= end) break;
  }
  return points;
}

/** The percent of an automated mix per sample (Effects.cs MixCurve): a fade, or auto/lfo/env points, or the points of a control; clamped to 0..100. */
export function mixPercent(textOrPoints, firstBar, samplesPerBar, rate, length) {
  if (Array.isArray(textOrPoints)) return curveAt(textOrPoints, firstBar, samplesPerBar, length, (v) => Math.max(0, Math.min(100, v)));
  const text = unquote(textOrPoints);
  if (/^(fadein|fadeout)\(/.test(text)) {
    const amount = volumeCurve(text, samplesPerBar, rate, firstBar);
    if (!amount) return null;
    const out = new Int32Array(length);
    for (let i = 0; i < length; i++) out[i] = Math.floor(Math.round(amount(i) * 32768) * 100 / 32768);
    return out;
  }
  const points = panPoints(text, samplesPerBar, rate);
  return points ? curveAt(points, firstBar, samplesPerBar, length, (v) => Math.max(0, Math.min(100, v))) : null;
}

/** Dry and wet blended by a percent per sample as the server (Blend, then a 16-bit clip); the channels are floats of whole 16-bit steps. */
export function blendMix(dry, wet, percent) {
  const channels = wet.channels.map((w, c) => {
    const d = dry.channels[c] || dry.channels[0], out = new Float32Array(w.length);
    for (let i = 0; i < w.length; i++) {
      const p = percent[Math.min(i, percent.length - 1)], a = Math.round((d[i] ?? 0) * 32768), b = Math.round(w[i] * 32768);
      out[i] = Math.max(-32768, Math.min(32767, idiv(a * (100 - p) + b * p, 100))) / 32768;
    }
    return out;
  });
  return { sampleRate: wet.sampleRate, channels };
}
