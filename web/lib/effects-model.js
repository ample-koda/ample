// Pure descriptions for the six built-in audio effect cards.
// @ample:effect-properties

import { readNumber } from './numfield-model.js';

export const EFFECTS = Object.freeze(['eq', 'distortion', 'delay', 'reverb', 'compressor', 'filter']);

const number = (params, name, fallback) => {
  const meta = params.find(name);
  return readNumber(meta ? params.values[name] ?? meta.default : fallback, null, meta?.unit).value ?? fallback;
};

const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

/** Keeps template names and order while adding the small visual data used by the effect card. */
export function effectModel(type, definitions, values) {
  if (!EFFECTS.includes(type)) return null;
  const params = { find: (name) => definitions.find((p) => p.name === name), values };
  const controls = definitions.filter((p) => p.name !== 'enabled').map((p) => ({ ...p, value: values[p.name] ?? p.default }));
  let visual = null;
  if (type === 'eq') {
    const low = number(params, 'low', 0), mid = number(params, 'mid', 0), high = number(params, 'high', 0);
    visual = { kind: 'curve', points: [low, mid, high].map((v, i) => [i * 50, 24 - clamp(v, -12, 12)]) };
  } else if (type === 'compressor') {
    visual = { kind: 'compression', threshold: number(params, 'threshold', -18), ratio: number(params, 'ratio', 4) };
  } else if (type === 'delay') {
    visual = { kind: 'delay', time: number(params, 'time', 250), feedback: number(params, 'feedback', 35) };
  } else if (type === 'reverb') {
    visual = { kind: 'space', size: number(params, 'size', 50), decay: number(params, 'decay', 60) };
  }
  return { type, enabled: values.enabled ?? definitions.find((p) => p.name === 'enabled')?.default ?? 'on', controls, visual };
}
