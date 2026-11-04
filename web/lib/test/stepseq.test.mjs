import test from 'node:test';
import assert from 'node:assert/strict';
import { NAMED, STEPS, beatOf, cycleCell, formatDrums, formatStrum, full, missingVoices, nameOf, next, parseDrums, parseStrum, DRUM_CYCLE, STRUM_CYCLE } from '../stepseq-model.js';

test('a named pattern reads as grids and writes back to its name', () => {
  const r = parseDrums('beat-A');
  assert.equal(r.named, 'beat-A');
  assert.deepEqual(r.voices.map((v) => v.name), ['kick', 'snare', 'hat']);
  assert.equal(r.voices[0].steps, 'x.......x.......');
  assert.equal(nameOf(r.voices), 'beat-A');
  assert.equal(nameOf(parseDrums('kick:x...x...x...x...').voices), null);
  assert.ok(Object.keys(NAMED).every((k) => parseDrums(k).voices.length === 3));
});

test('voice grids are read, widened to sixteen steps for the editor and written back as short as they were (the engine repeats a short grid)', () => {
  const r = parseDrums('kick:x... snare:..X.|hat:xo');
  assert.equal(r.error, null);
  assert.deepEqual(r.voices.map((v) => v.steps), ['x...x...x...x...', '..X...X...X...X.', 'xoxoxoxoxoxoxoxo']);
  assert.equal(formatDrums(r.voices), 'kick:x... snare:..X. hat:xo');
  assert.equal(full(''), '.'.repeat(STEPS));
  assert.equal(full('x.'), 'x.x.x.x.x.x.x.x.');
});

test('bad patterns give an error and no voices', () => {
  assert.match(parseDrums('kick').error, /not voice:steps/);
  assert.match(parseDrums('kick:xyz').error, /may only hold/);
  assert.match(parseDrums('').error, /empty/);
  assert.equal(parseDrums('kick:xyz').voices.length, 0);
});

test('a click goes through rest, hit, accent, ghost and back', () => {
  let c = '.';
  const seen = [];
  for (let i = 0; i < 5; i++) { c = next(DRUM_CYCLE, c); seen.push(c); }
  assert.deepEqual(seen, ['x', 'X', 'o', '.', 'x']);
  assert.equal(cycleCell('x...', 1), 'xx..x...x...x...'.slice(0, 0) + 'xx..x...x...x...');
  assert.equal(cycleCell('................', 3), '...x............');
  assert.deepEqual([...STRUM_CYCLE].map((c2) => next(STRUM_CYCLE, c2)), ['D', 'd', 'U', 'u', 'x', '.']);
});

test('strums are read per bar and keep their bars', () => {
  const r = parseStrum('D...D.u.|d');
  assert.equal(r.error, null);
  assert.equal(r.bars.length, 2);
  assert.equal(r.bars[0], 'D...D.u.D...D.u.');
  assert.equal(formatStrum(r.bars), 'D...D.u.D...D.u.|dddddddddddddddd');
  assert.match(parseStrum('Z...').error, /may only hold/);
  assert.match(parseStrum('').error, /empty/);
});

test('voices the kit does not have are named, and steps know their place in the bar', () => {
  assert.deepEqual(missingVoices(parseDrums('kick:x tom:x hat:x').voices, ['kick', 'snare', 'hat']), ['tom']);
  assert.deepEqual([0, 1, 2, 3, 4, 6].map((i) => beatOf(i)), ['beat', 'sixteenth', 'eighth', 'sixteenth', 'beat', 'eighth']);
});

test('voice options (mute, level, pan) read and write back, and solo mutes the others', async () => {
  const { parseDrums: parse, formatDrums: format, toggleSolo: solo } = await import('../stepseq-model.js');
  const r = parse('kick:x...@l-3,p-8 snare:..x.@m');
  assert.deepEqual(r.voices.map((v) => [v.mute, v.level, v.pan]), [[false, -3, -8], [true, 0, 0]]);
  assert.equal(format(r.voices), 'kick:x...@l-3,p-8 snare:..x.@m');
  assert.match(parse('kick:x...@q').error, /not an option/);
  const open = r.voices.map((v) => ({ ...v, mute: false }));
  const soloed = solo(open, 0);
  assert.deepEqual(soloed.map((v) => v.mute), [false, true]);
  assert.deepEqual(solo(soloed, 0).map((v) => v.mute), [false, false]);
});

test('the grid of a drums block is as wide as its steps parameter', async () => {
  const m = await import('../stepseq-model.js');
  assert.equal(m.stepsOfBlock('8'), 8);
  assert.equal(m.stepsOfBlock('"32"'), 32);
  assert.equal(m.stepsOfBlock(undefined), 16);
  assert.equal(m.stepsOfBlock('0'), 16);
  assert.equal(m.stepsOfBlock('99'), 64);
  const eight = m.parseDrums('kick:x...x...', 8);
  assert.equal(eight.voices[0].steps, 'x...x...');
  assert.equal(eight.voices[0].length, 8);
  assert.equal(m.parseDrums('kick:x...', 8).voices[0].steps, 'x...x...');   // a shorter grid repeats
  assert.equal(m.formatDrums(eight.voices, 8), 'kick:x...x...');
  assert.equal(m.cycleCell('x.......', 3, undefined, 8), 'x..x....');
  assert.equal(m.withLength(eight.voices[0], 4, 8).steps, 'x...x...');
  assert.equal(m.withVelocity('x.......', 2, 5, 8), 'x.5.....');
  // a name is only a name at 16 steps
  const a = m.parseDrums('beat-A');
  assert.equal(m.nameOf(a.voices), 'beat-A');
  assert.equal(m.nameOf(m.parseDrums('beat-A', 8).voices, 8), null);
});

test('the beats of a bar are where the steps divide evenly, with any number of steps', async () => {
  const { beatOf, stepsPerBeat } = await import('../stepseq-model.js');
  assert.equal(stepsPerBeat(16), 4);
  assert.equal(stepsPerBeat(8), 2);
  assert.deepEqual([0, 1, 2, 3].map((i) => beatOf(i, 8)), ['beat', 'eighth', 'beat', 'eighth']);
  assert.deepEqual([0, 1, 2, 4, 8].map((i) => beatOf(i, 32)), ['beat', 'sixteenth', 'sixteenth', 'eighth', 'beat']);
});

test('a strum has the width of the steps of its block, and a strum of whole bars is read as those bars', async () => {
  const { parseStrum, formatStrum } = await import('../stepseq-model.js');
  assert.deepEqual(parseStrum('D...D.u.', 8).bars, ['D...D.u.']);
  assert.deepEqual(parseStrum('D...', 8).bars, ['D...D...']);               // a shorter strum repeats in the bar
  const long = parseStrum('D...D.u.D.u.D...', 8);                          // 16 characters with 8 steps are two bars
  assert.deepEqual(long.bars, ['D...D.u.', 'D.u.D...']);
  assert.equal(formatStrum(long.bars), 'D...D.u.|D.u.D...');
  assert.deepEqual(parseStrum('D...D.u.D.u.', 16).bars, ['D...D.u.D.u.D...']);   // 12 characters are one bar of 16 (repeated to fill it), not whole bars
  assert.deepEqual(parseStrum('', 8).bars, ['........']);
});
