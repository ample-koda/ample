// Plan local-preview 1.1: for every parameter that the live table (live-parameters.mjs) calls 'param', two values to try (the template default and a far one) so that a test can move
// the knob in the middle of a render and check that the sound changes. Pure and without a browser: the spec feeds it the template parameters and renders the result.
import { LIVE_PARAMETERS } from './live-parameters.mjs';

const split = (text) => { const m = /^\s*(-?\d+(?:\.\d+)?)\s*(.*)$/.exec(String(text)); return m ? { value: Number(m[1]), suffix: m[2] } : null; };
const show = (value, suffix) => `${Number(value.toFixed(3))}${suffix ? ` ${suffix}` : ''}`;

/** Blocks whose parameters are not played by liveEffectGraph (own modules with their own specs), and why. */
export const PROBE_SKIPPED_TYPES = {
  mixer: 'the live mixer has its own graph and spec (live-mixer.spec.ts)',
  send: 'a send is a sum with two outputs in the live chain, covered by live-routing.spec.ts',
  bus: 'the live mixer has its own graph and spec (live-mixer.spec.ts)',
  duck: 'follows the playing take on the audio clock (live-clock-gate), covered by live-preview-gaps.spec.ts',
};

/** Per parameter: replace the derived pair ({ a, b }), set a different starting point ({ base }), or skip with a reason ({ skip }). Key: `${type}.${name}`. */
export const PROBE_OVERRIDES = {
  'compressor.keyFilter': { skip: 'only filters the sidechain key, which a single block does not have (live-sidechain.spec.ts)' },
  'tremolo.rate': { base: { sync: 'off' } },
  'delay.feedback': { base: { time: '120 ms', sync: 'off' } },
  'delay.enabled': { base: { time: '120 ms', sync: 'off' } },
  'delay.mix': { base: { sync: 'off' } },
  'delay.time': { a: '120 ms', b: '480 ms', base: { sync: 'off' } },
  'delay.sync': { a: 'off', b: '1/4', base: { time: '120 ms' } },
  'gate.attack': { base: { threshold: '-15 dB', hold: '20 ms' } },
  'gate.release': { base: { threshold: '-15 dB', hold: '20 ms' } },
  'gate.enabled': { base: { threshold: '-15 dB', hold: '20 ms' } },
  'gate.threshold': { a: '-50 dB', b: '-10 dB', base: { hold: '20 ms' } },
  ...Object.fromEntries([2, 3, 4, 5].map((k) => [`peq.band${k}_q`, { base: { [`band${k}_type`]: 'peak' } }])),   // a shelf has no Q
  'deesser.frequency': { a: '3100 Hz', b: '9000 Hz', base: { threshold: '-40 dB', ratio: '8' } },
  'deesser.threshold': { a: '-40 dB', b: '-6 dB', base: { frequency: '3100 Hz', ratio: '8' } },
  'deesser.ratio': { a: '1', b: '16', base: { frequency: '3100 Hz', threshold: '-40 dB' } },
  'deesser.enabled': { base: { frequency: '3100 Hz', threshold: '-40 dB', ratio: '8' } },
  'tremolo.pattern': { skip: 'a gate on the audio clock (live-clock-gate), covered by live-preview-gaps.spec.ts' },
  'tremolo.sync': { skip: 'a gate on the audio clock (live-clock-gate), covered by live-preview-gaps.spec.ts' },
};

/** The values of one parameter: { a, b } as strings, or null when the pair cannot be derived. */
export function valuesFor(parameter) {
  const options = parameter.options, def = parameter.default;
  if (Array.isArray(options) && options.length > 1) { const other = options.find((o) => o !== def); return other === undefined ? null : { a: def, b: other }; }
  const d = split(def);
  if (!d || parameter.min == null || parameter.max == null) return null;
  const { min, max } = parameter, unit = d.suffix;
  const up = max - d.value >= d.value - min;
  let b;
  if (min > 0 && max / min > 20) b = up ? d.value * (max / d.value) ** .8 : d.value * (min / d.value) ** .8;   // frequency-like: move on a log scale
  else b = up ? d.value + (max - d.value) * .8 : d.value - (d.value - min) * .8;
  return { a: def, b: show(b, unit) };
}

/** All probes of one block type: [{ param, a, b, skip? }]. `parameters` is the `parameters` array of spec/templates/<type>.json. */
export function probesFor(type, parameters) {
  const entry = LIVE_PARAMETERS[type];
  if (!entry?.param) return [];
  const byName = new Map(parameters.map((p) => [p.name, p]));
  const names = [...entry.param]; if (!names.includes('enabled') && byName.has('enabled')) names.push('enabled');
  return names.map((param) => {
    const key = `${type}.${param}`, override = PROBE_OVERRIDES[key], template = byName.get(param);
    if (PROBE_SKIPPED_TYPES[type]) return { param, skip: PROBE_SKIPPED_TYPES[type] };
    if (override?.skip) return { param, skip: override.skip };
    const pair = override?.a !== undefined ? override : template && valuesFor(template);
    if (!pair) return { param, skip: 'no numeric range or options in the template' };
    // the other knobs sit on their far values, so a block that is neutral at its defaults (a flat equaliser, a gate that is open) still has something to change
    const base = {};
    for (const other of names) { if (other === param || other.endsWith('enabled')) continue; const far = byName.get(other) && valuesFor(byName.get(other)); if (far) base[other] = far.b; }
    return { param, a: pair.a, b: pair.b, base: { ...base, ...(override?.base || {}) } };
  });
}
