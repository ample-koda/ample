// The gain of a volume block, as the server computes it (VolumeRenderer.cs, Automation.cs, Fades.cs), for the offline preview and the live graph. All of it is whole numbers on the server (Q15 gains,
// truncating divisions); here the same arithmetic, so a volume with auto(...), lfo(...), env(...), fadein(...) or fadeout(...) follows the render.
//  - a static gain is the whole dB of the parameter (-12.041 is -12) taken from the gain table of the server (GainFromDbWide: 32768 * 1.12201845...^dB, rounded, -60..+48 dB);
//  - auto/lfo/env: the gains of the points joined by straight lines in the gain (not in dB), the time counted from the start of the song, the take from bar `firstBar`;
//  - fadein/fadeout(start, length[, lin|equal|exp]): the amount in Q15 per sample, equal power with an integer square root.
import { panPoints } from './live-pan-auto.mjs';

const STEP = 1.1220184543019634355910389464;
/** The Q15 gain of a whole number of dB, -60..+48 (GainFromDbWide). */
export const gainQ15Wide = (db) => Math.round(32768 * STEP ** Math.max(-60, Math.min(48, db)));

const FADE = /^(fadein|fadeout)\((.*)\)$/s, POSITION = /^(\d{1,6})(?:\.(\d{1,6}))?\s*(s)?$/;
const micro = (text) => { const m = POSITION.exec(text.trim()); return m ? { value: BigInt(m[1]) * 1000000n + BigInt((m[2] || '').padEnd(6, '0')), seconds: Boolean(m[3]) } : null; };

/** The fade of a text as { fadeIn, start, span (samples), shape }, or null when it is not a fade the server reads. */
function parseFade(text, samplesPerBar, rate) {
  const m = FADE.exec(String(text ?? '').trim());
  if (!m) return null;
  const parts = m[2].split(',').map((p) => p.trim());
  if (parts.length < 2 || parts.length > 3) return null;
  const start = micro(parts[0]), length = micro(parts[1]);
  if (!start || !length || length.value <= 0n || (!start.seconds && start.value < 1000000n)) return null;
  const shape = (parts[2] || 'lin').toLowerCase();
  if (!['lin', 'linear', 'equal', 'exp'].includes(shape)) return null;
  const bars = BigInt(samplesPerBar), hz = BigInt(rate);
  const at = start.seconds ? start.value * hz / 1000000n : (start.value - 1000000n) * bars / 1000000n;
  const span = length.seconds ? length.value * hz / 1000000n : length.value * bars / 1000000n;
  return { fadeIn: m[1] === 'fadein', start: Number(at), span: Math.max(1, Number(span)), shape: shape === 'linear' ? 'lin' : shape };
}
const shapeOf = (shape, p) => (shape === 'equal' ? Math.floor(Math.sqrt(p * 32768)) : shape === 'exp' ? Math.floor(p * p / 32768) : p);

/** The gain (Q15 / 32768) at sample i of a take that starts at bar `firstBar` of the song, as a function cheap to call in order; null when the text is not an automation the server reads. */
export function volumeCurve(text, samplesPerBar, rate, firstBar = 1) {
  const fade = parseFade(text, samplesPerBar, rate), origin = (Math.max(1, firstBar) - 1) * samplesPerBar;
  if (fade) {
    const end = fade.start + fade.span;
    return (i) => {
      const at = origin + i, p = at <= fade.start ? 0 : at >= end ? 32768 : Math.floor((at - fade.start) * 32768 / fade.span);
      return shapeOf(fade.shape, fade.fadeIn ? p : 32768 - p) / 32768;
    };
  }
  const points = panPoints(text, samplesPerBar, rate);
  if (!points) return null;
  const gains = points.map((p) => gainQ15Wide(p.value));
  let segment = 0;
  return (i) => {
    const absolute = origin + i;
    if (segment > 0 && absolute < points[segment].sample) segment = 0;
    while (segment + 1 < points.length && absolute >= points[segment + 1].sample) segment++;
    if (absolute < points[0].sample || segment + 1 >= points.length) return (absolute < points[0].sample ? gains[0] : gains[gains.length - 1]) / 32768;
    const from = points[segment].sample, to = points[segment + 1].sample;
    return (gains[segment] + Math.trunc((gains[segment + 1] - gains[segment]) * (absolute - from) / Math.max(1, to - from))) / 32768;
  };
}

/** The sample of the song where the gain first changes (a window of a test starts a little before it), 0 when there is none. */
export function automationFocus(text, samplesPerBar, rate) {
  const fade = parseFade(text, samplesPerBar, rate);
  if (fade) return fade.start;
  const points = panPoints(text, samplesPerBar, rate);
  if (!points) return 0;
  const index = points.findIndex((p, i) => i + 1 < points.length && points[i + 1].value !== p.value);
  return index < 0 ? 0 : points[index].sample;
}
