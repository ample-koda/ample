// The pure part of the step editors of the sample drums and the guitar: reading and writing the pattern and strum texts. Twin of the
// parsers in src/Ample.Audio/Engine/SamplerRenderers.cs (the same grammar); tested in web/lib/test/stepseq.test.mjs.
// @ample:samples

import { barBeats, stepsPerBar } from './meter.mjs';

/** The steps of a bar in 4/4; the grid of the open song is stepsPerBar() (12 in 3/4 and 6/8, 20 in 5/4), or the steps parameter of a drums block (1 to 64). */
export const STEPS = 16;

/** The width of the grid of a drums block: its steps parameter (1 to 64), else the steps of a bar of the song. */
export function stepsOfBlock(text) {
  const n = Math.round(Number(String(text ?? '').replace(/^"|"$/g, '').trim()));
  return Number.isFinite(n) && n >= 1 ? Math.min(n, 64) : stepsPerBar();
}

/** How many steps make a beat of the bar (4 in 4/4 with 16 steps, 2 with steps 8). */
export const stepsPerBeat = (width = stepsPerBar()) => Math.max(1, width / Math.max(1, barBeats()));

/** The named drum patterns of the engine, written as grids. */
export const NAMED = {
  'beat-A': 'kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x.',
  'beat-B': 'kick:x.....x.x.....x. snare:....x.......x... hat:x.x.x.x.x.x.x.x.',
};

/** Order in which a click goes through the cell values of a drum step: rest, hit, accent, ghost, rest. */
export const DRUM_CYCLE = ['.', 'x', 'X', 'o'];

/** Shift+click: a step with an exact velocity, the digit 1 (softest) to 9 (loudest): rest, 3, 5, 7, 9, rest. */
export const VELOCITY_CYCLE = ['.', '3', '5', '7', '9'];

/** Order for a strum step: rest (let it ring), down, soft down, up, soft up, mute. */
export const STRUM_CYCLE = ['.', 'D', 'd', 'U', 'u', 'x'];

export const next = (cycle, c) => cycle[(cycle.indexOf(c) + 1) % cycle.length] ?? cycle[0];

/** Pads or repeats a grid to the width of the grid (a shorter grid repeats, as the engine plays it). */
export function full(steps, width = stepsPerBar()) {
  if (!steps.length) return '.'.repeat(width);
  return Array.from({ length: width }, (_, i) => steps[i % steps.length]).join('');
}

/**
 * Reads a drum pattern: a name or voice grids "kick:x... snare:..x." into { voices: [{ name, steps }], named, error }.
 * The grids are widened to 16 steps. An empty or unreadable text gives no voices and an error text.
 */
export function parseDrums(text, width = stepsPerBar()) {
  const value = String(text ?? '').trim().replace(/^"|"$/g, '');
  const named = Object.hasOwn(NAMED, value) ? value : null;
  const grid = named ? NAMED[named] : value;
  const voices = [];
  for (const part of grid.split(/[\s|;]+/).filter(Boolean)) {
    const colon = part.indexOf(':');
    if (colon <= 0) return { voices: [], named: null, error: `"${part}" is not voice:steps` };
    let steps = part.slice(colon + 1);
    let mute = false, level = 0, pan = 0, swing = -1, tune = 0;
    const at = steps.indexOf('@');
    if (at >= 0) {
      for (const option of steps.slice(at + 1).split(',').map((o) => o.trim()).filter(Boolean)) {
        if (option === 'm') mute = true;
        else if (/^l-?\d+$/.test(option)) level = Math.max(-24, Math.min(12, Number(option.slice(1))));
        else if (/^p-?\d+$/.test(option)) pan = Math.max(-32, Math.min(32, Number(option.slice(1))));
        else if (/^s\d+$/.test(option)) swing = Math.max(0, Math.min(60, Number(option.slice(1))));
        else if (/^t-?\d+$/.test(option)) tune = Math.max(-24, Math.min(24, Number(option.slice(1))));
        else return { voices: [], named: null, error: `"${option}" is not an option of ${part.slice(0, colon)} (m, l<dB>, p<pan>, s<swing> or t<semitones>)` };
      }
      steps = steps.slice(0, at);
    }
    if (/[^xXo.1-9]/.test(steps)) return { voices: [], named: null, error: `the grid of ${part.slice(0, colon)} may only hold x, X, o, the digits 1 to 9 and .` };
    voices.push({ name: part.slice(0, colon).toLowerCase(), steps: full(steps, width), length: Math.min(width, Math.max(1, steps.length)), mute, level, pan, swing, tune });
  }
  return { voices, named, error: voices.length ? null : 'the pattern is empty' };
}

/** Writes voices back as one text line. */
export const optionsOf = (v) => [v.mute ? 'm' : '', v.level ? `l${v.level}` : '', v.pan ? `p${v.pan}` : '', v.swing >= 0 ? `s${v.swing}` : '', v.tune ? `t${v.tune}` : ''].filter(Boolean).join(',');
export const formatDrums = (voices, width = stepsPerBar()) => voices.map((v) => `${v.name}:${v.steps.slice(0, v.length ?? width)}${optionsOf(v) ? `@${optionsOf(v)}` : ''}`).join(' ');

/** Solo: every other voice is muted (written as text, so it is part of the source); soloing the only unmuted voice again unmutes all. */
export function toggleSolo(voices, index) {
  const alone = voices.every((v, i) => (i === index ? !v.mute : v.mute));
  return voices.map((v, i) => ({ ...v, mute: alone ? false : i !== index }));
}

/** The name of a named pattern when the voices say exactly that, else null. */
export function nameOf(voices, width = stepsPerBar()) {
  // the names are 16-step grids: with another width of the grid a pattern is never written as a name
  if (width !== 16) return null;
  const text = formatDrums(voices.map((v) => ({ ...v, steps: full(v.steps.slice(0, v.length ?? width), width), length: width })), width);
  return Object.entries(NAMED).find(([, grid]) => grid === text)?.[0] ?? null;
}

/** Reads a strum: bars separated by | (or spaces), 16 steps each. Gives an array of bars (strings of 16) and an error text or null. */
export function parseStrum(text, width = stepsPerBar()) {
  // a strum of a whole number of bars (16 characters with 8 steps in a bar) is read as those bars, as the engine plays it
  const bars = String(text ?? '').trim().replace(/^"|"$/g, '').split(/[|\s]+/).filter(Boolean).flatMap((b) => (b.length > width && b.length % width === 0 ? Array.from({ length: b.length / width }, (_, i) => b.slice(i * width, (i + 1) * width)) : [b]));
  if (!bars.length) return { bars: ['.'.repeat(width)], error: 'the strum is empty' };
  if (bars.some((b) => /[^.xDdUu]/.test(b))) return { bars: bars.map((b) => full(b, width)), error: 'a strum may only hold D d U u x and .' };
  return { bars: bars.map((b) => full(b, width)), error: null };
}

/** Writes bars back. Bars that are all rests at the end are kept (the engine cycles through the bars, so their number matters). */
export const formatStrum = (bars) => bars.join('|');

/** A drum grid with one cell changed to the next value of the cycle. */
export function cycleCell(steps, index, cycle = DRUM_CYCLE, width = stepsPerBar()) {
  const cells = [...full(steps, width)];
  cells[index] = next(cycle, cells[index]);
  return cells.join('');
}

/** The voices a pattern uses that the kit does not have (lower case file names without .wav). */
export const missingVoices = (voices, files) => voices.map((v) => v.name).filter((n) => !files.includes(n));

/** Which beat of the bar a step is on (with 16 steps: 0, 4, 8, 12 are beats; the others are the eighths and sixteenths between them; with another width the beats are where the steps divide evenly). */
export const beatOf = (step, width = stepsPerBar()) => { const per = stepsPerBeat(width); return Number.isInteger(per) ? (step % per === 0 ? 'beat' : per % 2 === 0 && step % (per / 2) === 0 ? 'eighth' : 'sixteenth') : (step % 4 === 0 ? 'beat' : step % 2 === 0 ? 'eighth' : 'sixteenth'); };

/** A new length for a voice (1 to the width of the grid): the grid keeps its first cells and repeats them, as the engine plays a shorter grid. */
export function withLength(voice, length, width = stepsPerBar()) {
  const n = Math.max(1, Math.min(width, Math.round(Number(length) || width)));
  return { ...voice, length: n, steps: full(voice.steps.slice(0, n), width) };
}

/** The velocity of a grid cell on a scale of 0 (rest) to 9 (loudest): ghost o is 4, hit x is 7, accent X is 9, a digit is itself. */
export const velocityOf = (c) => (/[1-9]/.test(c) ? Number(c) : { o: 4, x: 7, X: 9 }[c] ?? 0);

/** A grid with one step set to a velocity from 0 (rest) to 9. */
export function withVelocity(steps, index, level, width = stepsPerBar()) {
  const cells = [...full(steps, width)];
  cells[index] = level <= 0 ? '.' : String(Math.min(9, Math.round(level)));
  return cells.join('');
}

/** Parses a drums block's `map` text ("voice:folder/sample;...", no extension) into a Map from voice (lower case) to its target. */
export function parseVoiceMap(text) {
  return new Map((text || '').split(';').map((rule) => rule.trim()).filter(Boolean).map((rule) => [rule.split(':')[0].trim().toLowerCase(), rule.slice(rule.indexOf(':') + 1).trim()]));
}

/** The project-relative WAV path that would actually play for `voice` of a `kit` folder, given its parsed `map` entries - a mapped entry
 * wins over the kit; '' when nothing can be resolved to one playable file (no kit, a mapped or kit `pack:` value, or an `.sfz` kit, which
 * has no single `<voice>.wav` to preview). */
export function voiceSamplePath(voice, entries, kit) {
  const mapped = entries.get(voice);
  if (mapped) return mapped.startsWith('pack:') ? '' : `${mapped}.wav`;
  return kit && !kit.startsWith('pack:') && !kit.toLowerCase().endsWith('.sfz') ? `${kit}/${voice}.wav` : '';
}

/** Parses a drums block's `choke` text ("voice:closer,closer;...") into a Map from voice (lower case) to its closer voice names (lower case). */
export function parseChoke(text) {
  return new Map((text || '').split(';').map((rule) => rule.trim()).filter(Boolean).map((rule) => [rule.split(':')[0].trim().toLowerCase(), rule.slice(rule.indexOf(':') + 1).split(',').map((v) => v.trim().toLowerCase()).filter(Boolean)]));
}

/** Formats a choke Map back into `choke` parameter text: voices in a stable (alphabetical) order, a voice with no closers left out. */
export function formatChoke(entries) {
  return [...entries].filter(([, closers]) => closers.length).sort(([a], [b]) => a.localeCompare(b)).map(([voice, closers]) => `${voice}:${closers.join(',')}`).join(';');
}
