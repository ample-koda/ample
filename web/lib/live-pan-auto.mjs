// An automated pan (pan auto(...), lfo(...), env(...)) for the live chain, as the server renders it (Effects.cs Panner, Automation.cs ParseSamples / CurveAt, PanTable): every point is a pair
// of gains from the pan table, the gains are joined by straight lines (in the gain, not in the pan position), a stereo sound is balanced (both gains over the centre gain, at most 1),
// the time is counted from the start of the song, the take from bar `firstBar`. lfo(period, low, high) is a triangle written out as points half a period apart, starting at `low`
// (bar 1, bar 1 + period/2, ... up to bar 257). All of it is integer on the server (Q15 gains, truncating division); here the same arithmetic, so the live pan follows the render.
const RANGE = 32, isqrt = (value) => Math.floor(Math.sqrt(value));
const TABLE_LEFT = Array.from({ length: 2 * RANGE + 1 }, (_, i) => isqrt((2 * RANGE - i) * 2 ** 24)), TABLE_RIGHT = Array.from({ length: 2 * RANGE + 1 }, (_, i) => isqrt(i * 2 ** 24));   // sqrt(t / 64) * 32768
const clampPan = (v) => Math.max(-RANGE, Math.min(RANGE, v));
/** The Q15 gains of one pan position for a stereo sound (the far side quieter, the near side left alone) or a mono one (the power split). */
export function panGainsQ15(pan, stereo = true) {
  const l = TABLE_LEFT[clampPan(pan) + RANGE], r = TABLE_RIGHT[clampPan(pan) + RANGE];
  return stereo ? { left: Math.min(32768, Math.trunc(l * 32768 / TABLE_LEFT[RANGE])), right: Math.min(32768, Math.trunc(r * 32768 / TABLE_RIGHT[RANGE])) } : { left: l, right: r };
}

const wholeNumber = (text, signed) => ((signed ? /^[+-]?[0-9]+$/ : /^[0-9]+$/).test(text) ? Number(text) : null);

/** The points [{ sample, value }] of an automation text, or null when it is not one the server reads (a control binding, a fade, a malformed text, a text that is not whole numbers). */
export function panPoints(text, samplesPerBar, rate) {
  if (Array.isArray(text)) return text.length ? text : null;   // the points of a control (clientside-control-curves.mjs connectedPoints)
  const t = String(text ?? '').trim(), call = /^(auto|lfo|env)\((.*)\)$/s.exec(t);
  if (!call) return null;
  const parts = call[2].split(',').map((p) => p.trim()).filter((p) => p !== '');
  const value = (p) => wholeNumber(p.replace(/dB/g, '').trim(), true);
  if (call[1] === 'auto') {
    const points = [];
    for (const part of parts) {
      const colon = part.indexOf(':');
      if (colon <= 0) return null;
      let position = part.slice(0, colon).trim(); const seconds = /s$/i.test(position);
      if (seconds) position = position.slice(0, -1).trim();
      const at = /^[+-]?(\d+\.?\d*|\.\d+)(e[+-]?\d+)?$/i.test(position) ? Number(position) : NaN, v = value(part.slice(colon + 1));
      if (!Number.isFinite(at) || at < 0 || (!seconds && at < 1) || v === null) return null;
      points.push({ sample: seconds ? Math.round(at * rate) : Math.round((at - 1) * samplesPerBar), value: v });
    }
    return points.length ? points.sort((a, b) => a.sample - b.sample) : null;
  }
  if (call[1] === 'lfo') {
    if (parts.length !== 3) return null;
    const period = wholeNumber(parts[0], false), low = value(parts[1]), high = value(parts[2]);
    if (period === null || period < 2 || period % 2 !== 0 || low === null || high === null) return null;
    const points = [];
    for (let bar = 1, n = 0; bar <= 257; bar += period / 2, n++) points.push({ sample: (bar - 1) * samplesPerBar, value: n % 2 === 0 ? low : high });
    return points;
  }
  if (parts.length !== 6) return null;
  const n = parts.map((p, i) => (i < 4 ? wholeNumber(p.replace(/dB/g, '').trim(), false) : value(p)));
  if (n.some((x) => x === null) || n[0] < 1 || n[1] < 1 || n[3] < 1) return null;
  const rise = n[0] + n[1], hold = rise + n[2], fall = hold + n[3], bars = [[n[0], n[4]], [rise, n[5]]];
  if (hold > rise) bars.push([hold, n[5]]);
  bars.push([fall, n[4]]);
  return bars.map(([bar, v]) => ({ sample: (bar - 1) * samplesPerBar, value: v }));
}

/** The gain (0..1, Q15 / 32768) of one side at sample `i` of the take: a function that is cheap when called in order. side: 'left' or 'right'. */
export function panCurve(points, firstBar, samplesPerBar, side, stereo = true) {
  const gains = points.map((p) => panGainsQ15(p.value, stereo)[side]);
  let segment = 0;
  return (i) => {
    const absolute = (firstBar - 1) * samplesPerBar + i;
    if (segment > 0 && absolute < points[segment].sample) segment = 0;
    while (segment + 1 < points.length && absolute >= points[segment + 1].sample) segment++;
    if (absolute < points[0].sample || segment + 1 >= points.length) return (absolute < points[0].sample ? gains[0] : gains[gains.length - 1]) / 32768;
    const from = points[segment].sample, to = points[segment + 1].sample;
    return (gains[segment] + Math.trunc((gains[segment + 1] - gains[segment]) * (absolute - from) / Math.max(1, to - from))) / 32768;
  };
}
