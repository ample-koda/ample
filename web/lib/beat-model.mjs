// The pure part of the beat wizard (plan templates-library T04): reading a beat of the catalogue, putting its voices on the voices of a kit, the text a drums block gets and whether the beat fits a tempo. @ample:beats

/** "kick:x...x... @l-6 hat:..x." -> [{ voice, steps, mods }]; a word that starts with @ belongs to the voice before it. */
export function parseGrid(text) {
  const out = [];
  for (const word of String(text ?? '').split(/\s+/).filter(Boolean)) {
    const voice = /^([A-Za-z][\w-]*):(.*)$/.exec(word);
    if (voice) out.push({ voice: voice[1].toLowerCase(), steps: voice[2], mods: [] });
    else if (word.startsWith('@') && out.length) out[out.length - 1].mods.push(word);
    else break;
  }
  return out;
}

/** The pattern a fill of the catalogue says ("snare:xxxx on the last bar" -> the grid before the words), or '' when the fill is only words. */
export function fillGridOf(text) {
  return parseGrid(String(text ?? '').split(/\s+/).filter((w, i, all) => { const stop = all.findIndex((x) => !/^([A-Za-z][\w-]*:|@)/.test(x)); return stop < 0 || i < stop; }).join(' '));
}

// what a kit may call a voice of the catalogue, best first
const ALIASES = {
  kick: ['kick', 'bd', 'bassdrum', 'kickdrum'],
  snare: ['snare', 'sd', 'snaredrum'],
  clap: ['clap', 'handclap', 'claps', 'snare'],
  rim: ['rim', 'rimshot', 'sidestick', 'stick', 'snare'],
  hat: ['hat', 'hihat', 'hh', 'closedhat', 'chh', 'closed'],
  openhat: ['open', 'openhat', 'ohh', 'oh', 'hat'],
  open: ['open', 'openhat', 'ohh', 'oh', 'hat'],
  ride: ['ride', 'cymbal', 'crash', 'hat'],
  crash: ['crash', 'cymbal', 'ride'],
  tom: ['tom', 'lowtom', 'floortom', 'hightom', 'kick'],
  shaker: ['shaker', 'maraca', 'hat'],
  cowbell: ['cowbell', 'bell', 'perc'],
  perc: ['perc', 'percussion', 'conga', 'bongo'],
  tambourine: ['tambourine', 'tamb', 'shaker', 'hat'],
};

/** For each voice of the beat the voice of the kit that plays it: the same name first, then a likely alternative; null when the kit has nothing that fits. */
export function mapVoices(beatVoices, kitVoices) {
  const kit = new Map(kitVoices.map((v) => [String(v).toLowerCase(), v]));
  const result = {};
  for (const voice of beatVoices) {
    const wanted = ALIASES[voice] ?? [voice];
    result[voice] = null;
    for (const name of [voice, ...wanted]) if (kit.has(name)) { result[voice] = kit.get(name); break; }
  }
  return result;
}

const orSteps = (a, b) => Array.from({ length: Math.max(a.length, b.length) }, (_, i) => (a[i] && a[i] !== '.' ? a[i] : b[i] ?? '.')).join('');

/** The grid on the voices of the kit: the voices are renamed (two that land on one voice are put together), a voice without a place is dropped and listed in `skipped`. */
export function gridOnKit(entries, mapping) {
  const byTarget = new Map(), skipped = [];
  for (const e of entries) {
    const target = mapping[e.voice];
    if (!target) { skipped.push(e.voice); continue; }
    const have = byTarget.get(target);
    if (have) have.steps = orSteps(have.steps, e.steps);
    else byTarget.set(target, { voice: target, steps: e.steps, mods: [...e.mods] });
  }
  return { grid: [...byTarget.values()].map((e) => [`${e.voice}:${e.steps}`, ...e.mods].join(' ')).join(' '), skipped };
}

/** Whether a beat fits a tempo: in its range, a double or half time feel, or outside. */
export function tempoFit(bpm, tempo) {
  if (!bpm || !tempo) return { kind: 'unknown', text: '' };
  if (bpm >= tempo.min && bpm <= tempo.max) return { kind: 'in range', text: `${bpm} bpm is in the range of this beat (${tempo.min}-${tempo.max}).` };
  if (bpm * 2 >= tempo.min && bpm * 2 <= tempo.max) return { kind: 'double', text: `At ${bpm} bpm this feels like double time: it fits at ${bpm * 2} bpm played with half the hits.` };
  if (bpm / 2 >= tempo.min && bpm / 2 <= tempo.max) return { kind: 'half', text: `At ${bpm} bpm this feels like half time: it fits at ${bpm / 2} bpm.` };
  return { kind: 'outside', text: `${bpm} bpm is outside the range of this beat (${tempo.min}-${tempo.max}). It still plays, it just may not feel right.` };
}

/**
 * The operations that add the beat as one drums block: the kit, the pattern, the song (the groove for the bars with the fill in the last bar), swing and humanize, and the place on the timeline.
 * o: { name, kit ('' = the built-in sound), grid, fill, bar, bars, swing, humanize, spot: { x, y } }
 */
export function drumsOps(o) {
  const ops = [{ type: 'addBlock', blockType: 'drums', name: o.name }];
  if (o.kit) ops.push({ type: 'setParameter', block: o.name, name: 'kit', value: o.kit });
  const patterns = [`groove=${o.grid}`, ...(o.fill ? [`fill=${o.fill}`] : [])].join(' / ');
  const song = o.fill && o.bars > 1 ? `groove*${o.bars - 1} fill` : `groove*${o.bars}`;
  ops.push({ type: 'setParameter', block: o.name, name: 'patterns', value: patterns });
  ops.push({ type: 'setParameter', block: o.name, name: 'song', value: song });
  if (Number(o.swing) > 0) ops.push({ type: 'setParameter', block: o.name, name: 'swing', value: String(Math.round(o.swing)) });
  if (Number(o.humanize) > 0) ops.push({ type: 'setParameter', block: o.name, name: 'humanize', value: String(Math.round(o.humanize)) });
  ops.push({ type: 'setParts', block: o.name, parts: [{ at: o.bar, length: o.bars, name: '' }] });
  if (o.spot) ops.push({ type: 'setLayout', block: o.name, x: o.spot.x, y: o.spot.y });
  return ops;
}

/** A block name from the id of a beat: letters, digits and - only. */
export const blockNameOf = (id) => String(id ?? 'beat').toLowerCase().replace(/[^a-z0-9-]+/g, '-').replace(/^-+|-+$/g, '') || 'beat';

/** The gain of a step: x is a normal hit, a digit 1-9 a level, a modifier like @l-6 a level in dB on the whole voice. */
export function stepGain(char, mods = []) {
  if (!char || char === '.') return 0;
  const base = /^[1-9]$/.test(char) ? Number(char) / 9 : 0.85;
  const db = mods.map((m) => /^@l(-?\d+(?:\.\d+)?)/.exec(m)).find(Boolean);
  return base * (db ? 10 ** (Number(db[1]) / 20) : 1);
}
