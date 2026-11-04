// Chords and bass of a groove-set (plan templates-library T04b): the progression of a beat in a key, the bass line on its roots, and all the operations that add a beat. @ample:beats
import { drumsOps } from './beat-model.mjs';

export const KEYS = ['C', 'C#', 'D', 'Eb', 'E', 'F', 'F#', 'G', 'Ab', 'A', 'Bb', 'B'];
const PC = { C: 0, 'C#': 1, D: 2, Eb: 3, E: 4, F: 5, 'F#': 6, G: 7, Ab: 8, A: 9, Bb: 10, B: 11 };
const SHARP = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
const FLAT = ['C', 'Db', 'D', 'Eb', 'E', 'F', 'Gb', 'G', 'Ab', 'A', 'Bb', 'B'];
const MAJOR = [0, 2, 4, 5, 7, 9, 11];
const MINOR = [0, 2, 3, 5, 7, 8, 10];
const DEGREE = { i: 0, ii: 1, iii: 2, iv: 3, v: 4, vi: 5, vii: 6 };

/** The mode of a beat ("minor", "major (jazz colours)") as 'minor' or 'major'. */
export const modeOf = (text) => (/^minor/i.test(String(text ?? '')) ? 'minor' : 'major');

/** The chord names (one per chord of the progression) of roman numerals in a key: "i - VI - III - VII" in A minor gives Am F C G. A progression that is no numerals ("one chord") is the tonic. */
export function progressionChords(text, mode, key = 'C') {
  const keyPc = PC[key] ?? 0;
  const names = key.includes('b') || key === 'F' ? FLAT : SHARP;
  const scale = mode === 'minor' ? MINOR : MAJOR;
  const words = String(text ?? '').split(/\s*\(|\s+or\s+/)[0].split(/\s*-\s*/).map((w) => w.trim()).filter(Boolean);
  const chords = [];
  for (const word of words) {
    const m = /^([b#]?)(VII|VI|IV|V|III|II|I|vii|vi|iv|v|iii|ii|i)(°|dim|maj7|M7|7|9)?$/.exec(word);
    if (!m) continue;
    const lower = m[2] === m[2].toLowerCase();
    const degree = DEGREE[m[2].toLowerCase()];
    // in a minor key the scale already has the flat third, sixth and seventh: a b in front of them says the same
    const shift = m[1] === 'b' && !(mode === 'minor' && [2, 5, 6].includes(degree)) ? -1 : m[1] === '#' ? 1 : 0;
    const root = names[(keyPc + scale[degree] + shift + 12) % 12];
    const q = m[3];
    if (lower) chords.push(root + (q === '7' ? 'm7' : q === '9' ? 'm9' : q === '°' || q === 'dim' ? 'dim' : q === 'maj7' || q === 'M7' ? 'mmaj7' : 'm'));
    else chords.push(root + (q === 'maj7' || q === 'M7' ? 'maj7' : q === '7' ? '7' : q === '9' ? '9' : q === '°' || q === 'dim' ? 'dim' : ''));
  }
  return chords.length ? chords : [names[keyPc] + (mode === 'minor' ? 'm' : '')];
}

/** The chords for a number of bars: the progression goes round, one chord a bar. */
export const chordsForBars = (chords, bars) => Array.from({ length: bars }, (_, i) => chords[i % chords.length]);

const rootPc = (chord) => {
  const name = /^([A-G][#b]?)/.exec(chord)[1];
  const flatOf = { 'A#': 'Bb', 'D#': 'Eb', 'G#': 'Ab', Db: 'C#', Gb: 'F#' };
  return PC[name] ?? PC[flatOf[name]] ?? 0;
};
const num = (x) => String(Number(x.toFixed(3)));

/** The notes parameter of a bass line: the pattern of a bar (x is a hit, a digit a softer one) on the root of each chord of the bars, as bar:beat:length:pitch:velocity. */
export function bassNotes(pattern, chordPerBar, octaveBase = 36) {
  const steps = String(pattern ?? '').replace(/\s+/g, '');
  const hits = [];
  for (let i = 0; i < steps.length; i++) if (steps[i] !== '.') hits.push(i);
  if (!steps.length || !hits.length) return '';
  const beatsPerStep = 4 / steps.length;
  const out = [];
  chordPerBar.forEach((chord, bar) => {
    const pitch = octaveBase + rootPc(chord);
    hits.forEach((step, k) => {
      const end = hits[k + 1] ?? steps.length;
      const velocity = /^[1-9]$/.test(steps[step]) ? Math.round(30 + (Number(steps[step]) / 9) * 90) : 100;
      out.push(`${bar + 1}:${num(step * beatsPerStep)}:${num((end - step) * beatsPerStep)}:${pitch}:${velocity}`);
    });
  });
  return out.join(' ');
}

/**
 * All the operations that add a beat: the drums, and when asked the chords (a midi clip and a piano) and the bass (a midi clip and a bass), each with its place and, when a target is given, a
 * connection of its sound to a mixer, bus or the master. o: the options of drumsOps plus { chords: ['Am', ...] | null (the chords of the piano), bassPattern: '' | pattern, rootChords (the chords the bass follows, else the chords), targets: [{ to, input } | null, ...] }
 * where the targets go in the order drums, chords, bass.
 */
export function beatOps(o) {
  const ops = [...drumsOps(o)];
  const targets = [...(o.targets ?? [])];
  const connect = (from, target) => { if (target) ops.push({ type: 'connect', from, output: 'audio', to: target.to, input: target.input }); };
  connect(o.name, targets.shift());
  const place = (block, k) => ops.push({ type: 'setLayout', block, x: (o.spot?.x ?? 40) + 40 * k, y: (o.spot?.y ?? 40) + 90 * k });
  let k = 1;
  if (o.chords?.length) {
    const clip = `${o.name}-chords`;
    const keys = `${o.name}-keys`;
    ops.push({ type: 'addBlock', blockType: 'midi-clip', name: clip }, { type: 'setParameter', block: clip, name: 'chords', value: chordsForBars(o.chords, o.bars).join(' ') }, { type: 'setParameter', block: clip, name: 'bars', value: String(o.bars) });
    ops.push({ type: 'addBlock', blockType: 'piano', name: keys }, { type: 'setParts', block: keys, parts: [{ at: o.bar, length: o.bars, name: '' }] }, { type: 'connect', from: clip, output: 'notes', to: keys, input: 'notes' });
    place(clip, k); place(keys, k + 1); k += 2;
    connect(keys, targets.shift());
  }
  if (o.bassPattern) {
    const clip = `${o.name}-bassline`;
    const bass = `${o.name}-bass`;
    const roots = o.rootChords?.length ? o.rootChords : o.chords?.length ? o.chords : [o.rootChord ?? 'C'];
    const chords = chordsForBars(roots, o.bars);
    ops.push({ type: 'addBlock', blockType: 'midi-clip', name: clip }, { type: 'setParameter', block: clip, name: 'notes', value: bassNotes(o.bassPattern, chords) }, { type: 'setParameter', block: clip, name: 'bars', value: String(o.bars) });
    ops.push({ type: 'addBlock', blockType: 'bass', name: bass }, { type: 'setParts', block: bass, parts: [{ at: o.bar, length: o.bars, name: '' }] }, { type: 'connect', from: clip, output: 'notes', to: bass, input: 'notes' });
    place(clip, k); place(bass, k + 1);
    connect(bass, targets.shift());
  }
  return ops;
}
