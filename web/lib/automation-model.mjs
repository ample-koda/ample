// The pure part of the automation editor (plan X06): reading and writing auto(bar:value, ...) texts. Twin of Automation.cs in Ample.Core.
// @ample:automation

/** The automatable parameters by block type, with their range, unit and whether the value axis is logarithmic. */
export const AUTOMATABLE = {
  volume: { gain: { min: -60, max: 48, unit: 'dB' } },
  pan: { pan: { min: -32, max: 32, unit: '' } },
  filter: { cutoff: { min: 20, max: 20000, unit: 'Hz', log: true }, resonance: { min: 0, max: 100, unit: '%' } },
  compressor: { threshold: { min: -60, max: 0, unit: 'dB' } },
  delay: { mix: { min: 0, max: 100, unit: '%' } },
  reverb: { mix: { min: 0, max: 100, unit: '%' } },
  chorus: { mix: { min: 0, max: 100, unit: '%' } },
  flanger: { mix: { min: 0, max: 100, unit: '%' } },
  phaser: { mix: { min: 0, max: 100, unit: '%' } },
  bitcrusher: { mix: { min: 0, max: 100, unit: '%' } },
  pitch: { semitones: { min: -24, max: 24, unit: 'st' } },
  'delay-llama': { vowel: { min: 0, max: 100, unit: '%' }, voice: { min: 0, max: 100, unit: '%' }, mouth: { min: 0, max: 100, unit: '%' } },
  'midi-clip': { bend: { min: -100, max: 100, unit: '%' } },
};

/** The range of an automatable parameter for a block with these parameters: a pitch curve with a range set is drawn in percent of that range (-100 to 100). */
export function automationRange(type, name, params = {}) {
  const range = AUTOMATABLE[type]?.[name];
  if (!range) return null;
  if (type === 'pitch' && name === 'semitones' && Number.parseFloat(String(params.range ?? '').replace(/^"|"$/g, '')) > 0) return { min: -100, max: 100, unit: '%' };
  return range;
}

export const isAutomation = (text) => /^\s*(auto|lfo|env|fadein|fadeout)\(/.test(String(text ?? ''));

/** The points of an auto(...) text as [{ bar, value }] sorted by bar; null when the text is something else (a number, an lfo, a malformed text). */
export function parseAuto(text) {
  const m = /^\s*auto\((.*)\)\s*$/s.exec(String(text ?? ''));
  if (!m) return null;
  const points = [];
  for (const part of m[1].split(',').map((p) => p.trim()).filter(Boolean)) {
    const pm = /^(\d+):\s*(-?\d+)\s*(?:dB|Hz|%)?$/i.exec(part);
    if (!pm || Number(pm[1]) < 1) return null;
    points.push({ bar: Number(pm[1]), value: Number(pm[2]) });
  }
  return points.length ? points.sort((a, b) => a.bar - b.bar) : null;
}

/** Writes points back: one point per bar (a later point on the same bar wins). */
export function formatAuto(points) {
  const byBar = new Map();
  for (const p of points) byBar.set(Math.max(1, Math.round(p.bar)), Math.round(p.value));
  return `auto(${[...byBar].sort((a, b) => a[0] - b[0]).map(([bar, value]) => `${bar}:${value}`).join(', ')})`;
}

/** The starting points for a parameter that is still a plain value: the value held from bar 1 to the end of the shown bars. */
export function startingPoints(valueText, fallback, bars) {
  const value = Number.parseFloat(String(valueText ?? '').replace(/[a-z %]/gi, ''));
  const v = Number.isFinite(value) ? value : fallback;
  return [{ bar: 1, value: v }, { bar: bars, value: v }];
}

/** The parameters the browser can run live on the sound that feeds the block (liveEffectGraph in app/studio/preview/audio.js): the rest is heard through a draft build. */
const LIVE = { volume: ['gain'], pan: ['pan'], filter: ['cutoff', 'resonance'], compressor: ['threshold'], delay: ['mix'], reverb: ['mix'], chorus: ['mix'], flanger: ['mix'], phaser: ['mix'] };
/** A sentence naming what Live can follow, built from the list above so it cannot drift. */
export const liveSupportedText = () => Object.entries(LIVE).map(([type, names]) => names.map((name) => `${type} ${name}`).join(', ')).join('; ');
export const liveSupported = (type, name) => Boolean(LIVE[type]?.includes(name));

/**
 * The value of a curve at a song position in bars (1-based, fractional): straight lines between the points, the first value before the first point and the last after the last one.
 * A logarithmic axis (a cutoff in Hz) is joined in the log domain, as the plot draws it. This is the picture's curve, close to but not the engine's integer maths: Live is approximate.
 */
export function automationValueAt(points, bar, range = {}) {
  if (!points?.length) return null;
  const list = [...points].sort((a, b) => a.bar - b.bar);
  if (bar <= list[0].bar) return list[0].value;
  const last = list[list.length - 1];
  if (bar >= last.bar) return last.value;
  let i = 0;
  while (list[i + 1].bar <= bar) i++;
  const a = list[i], b = list[i + 1], f = (bar - a.bar) / Math.max(1e-9, b.bar - a.bar);
  if (range.log && a.value > 0 && b.value > 0) return Math.exp(Math.log(a.value) + (Math.log(b.value) - Math.log(a.value)) * f);
  return a.value + (b.value - a.value) * f;
}

/** The window of bars a preview plays around a bar: `size` bars centred on it, kept inside 1 to `totalBars` (when that is known). */
export function previewWindow(centerBar, totalBars, size = 8) {
  const total = Number(totalBars) > 0 ? Number(totalBars) : Infinity;
  const bars = Math.max(1, Math.min(size, total));
  let first = Math.round(centerBar - bars / 2);
  first = Math.max(1, Math.min(first, total - bars + 1));
  return { firstBar: first, bars };
}
