import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { moveNotes, absolute, addNote, keyToPitch, pasteNotes, recordedToNotes, entryText, format, moveNote, parse, parseEntry, quantizeNotes, removeNote, resizeNote, setVelocity, transposeNotes, updateNote } from '../notes-model.mjs';

const SAMPLES = [
  '1:0:1:60:96 1:1:1:64:96 2:0:4:67:100',
  '  1:0:1:60:96    1:1.5:0.5:64:96\n\t2:0:3.999999:67:100  ',
  '"1:0:1:60:96 1:1:1:64:96"',
  ' "1:0:1:60:96\n   1:1:1:64:96" ',
  '1:0:1:60:96:0:0:5 1:1:1:64:96:9:0:0:42',
  '1:0:1:60:96 garbage 1:1:1:64:96 7:7:7',
  '',
  '   ',
  '1:0:1:60:96',
];

test('format(parse(text)) is the text itself, for every kind of text', () => {
  for (const text of SAMPLES) assert.equal(format(parse(text)), text, JSON.stringify(text));
  for (const text of ['1:0:480:60:96 1:480:480:64:96', '3:240:240:72:1']) assert.equal(format(parse(text, 480)), text);
});

test('entries it does not understand are kept as raw and reported', () => {
  const model = parse('1:0:1:60:96 garbage 1:1:1:64:96 7:7:7');
  assert.equal(model.notes.length, 2);
  assert.equal(model.representable, false);
  assert.equal(model.reasons.length, 2);
  assert.equal(model.items.filter((i) => i.kind === 'raw').length, 2);
});

test('the expanded form is not read (it is edited in code) and says so', () => {
  const model = parse('notes {\n  note {\n    bar 1\n  }\n}');
  assert.equal(model.representable, false);
  assert.match(model.reasons[0], /expanded form/);
  assert.equal(model.notes.length, 0);
});

test('a note has its values, microbeat times and optional routing and tie', () => {
  assert.deepEqual(parseEntry('2:1.5:0.5:64:100', 1_000_000), { bar: 2, start: 1_500_000, length: 500_000, pitch: 64, velocity: 100 });
  assert.deepEqual(parseEntry('1:0:1:60:96:3:0:5:42', 1_000_000), { bar: 1, start: 0, length: 1_000_000, pitch: 60, velocity: 96, channel: 3, bank: 0, program: 5, tie: 42 });
  assert.equal(parseEntry('1:0:5:60:96', 1_000_000), null);          // longer than the bar
  assert.equal(parseEntry('1:4:1:60:96', 1_000_000), null);          // starts at the bar line of the next bar
  assert.equal(parseEntry('1:0:1:128:96', 1_000_000), null);
  assert.equal(parseEntry('1:0:1:60:0', 1_000_000), null);
  assert.equal(parseEntry('1:0:1:60:96:1:2', 1_000_000), null);      // routing needs channel, bank and program
});

test('one edit changes one entry and leaves all other text byte for byte', () => {
  const text = '1:0:1:60:96   1:1.50:1:64:96\n2:0:4:67:100';
  const model = parse(text);
  const next = format(moveNote(model, 1, 250_000, 2));
  assert.equal(next, '1:0:1:60:96   1:1.75:1:66:96\n2:0:4:67:100');
  assert.equal(format(setVelocity(model, 0, 64)), '1:0:1:60:96'.replace('96', '64') + text.slice('1:0:1:60:96'.length));
});

test('a move stays inside the bar: the start is clamped and the length shortened', () => {
  const model = parse('1:3:1:60:96');
  const moved = moveNote(model, 0, 900_000, 0);
  assert.equal(moved.notes[0].note.start, 3_900_000);
  assert.equal(moved.notes[0].note.length, 100_000);
  assert.equal(moveNote(parse('1:3:1:60:96'), 0, 9_000_000, 0).notes[0].note.start, 3_999_999);
  assert.equal(resizeNote(parse('1:2:1:60:96'), 0, 9_000_000).notes[0].note.length, 2_000_000);
  assert.equal(resizeNote(parse('1:0:1:60:96'), 0, 0).notes[0].note.length, 1);
});

test('a resize spans several bars and shrinks the same logical note without changing implicit routing or neighbours', () => {
  const source = parse('"1:3:1:60:96;pan=-20;future=7  3:0:1:67:90"');
  const grown = resizeNote(source, 0, 9_000_000), text = format(grown), reparsed = parse(text);
  assert.equal(reparsed.representable, true);
  const chain = reparsed.notes.filter((item) => item.note.pitch === 60);
  assert.equal(chain.reduce((sum, item) => sum + item.note.length, 0), 9_000_000);
  assert.equal(chain.length, 3);
  assert.ok(chain.every((item) => item.note.channel === undefined && item.note.pan === -20 && item.note.extra === ';future=7'));
  assert.ok(text.includes('3:0:1:67:90'));
  const shrunk = resizeNote(reparsed, 0, 500_000);
  assert.equal(shrunk.notes.filter((item) => item.note.pitch === 60).length, 1);
  assert.equal(shrunk.notes[0].note.tie, undefined);
  assert.equal(shrunk.notes[0].note.length, 500_000);
  const louder = setVelocity(reparsed, 0, 110);
  assert.ok(louder.notes.filter((item) => item.note.pitch === 60).every((item) => item.note.velocity === 110));
  const removed = removeNote(reparsed, 0);
  assert.equal(removed.notes.length, 1);
  assert.equal(removed.notes[0].note.pitch, 67);
});

test('add and remove keep the neighbouring text tidy', () => {
  const model = parse('1:0:1:60:96 1:1:1:64:96 1:2:1:67:96');
  assert.equal(format(removeNote(model, 1)), '1:0:1:60:96 1:2:1:67:96');
  assert.equal(format(removeNote(model, 0)), '1:1:1:64:96 1:2:1:67:96');
  assert.equal(format(removeNote(model, 2)), '1:0:1:60:96 1:1:1:64:96');
  assert.equal(format(addNote(model, { bar: 2, start: 0, length: 1_000_000, pitch: 72, velocity: 90 })), '1:0:1:60:96 1:1:1:64:96 1:2:1:67:96 2:0:1:72:90');
  assert.equal(format(addNote(parse(''), { bar: 1, start: 0, length: 1_000_000, pitch: 60, velocity: 96 })), '1:0:1:60:96');
  assert.equal(format(removeNote(parse('1:0:1:60:96'), 0)), '');
});

test('raw entries survive every edit', () => {
  const model = parse('1:0:1:60:96 garbage 1:1:1:64:96');
  assert.equal(format(transposeNotes(model, [0, 1], 12)), '1:0:1:72:96 garbage 1:1:1:76:96');
  assert.equal(format(removeNote(model, 0)), 'garbage 1:1:1:64:96');
});

test('quantize snaps the start and shortens a note that would pass the bar line', () => {
  const model = parse('1:0.13:1:60:96 1:3.9:0.1:64:96');
  assert.equal(format(quantizeNotes(model, [0, 1], 250_000)), '1:0.25:1:60:96 1:3.999999:0.000001:64:96');
});

test('with whole ticks as time (ppq 480) times stay integers', () => {
  const model = parse('1:0:480:60:96 2:240:240:64:96', 480);
  assert.equal(format(moveNote(model, 1, 120, 0)), '1:0:480:60:96 2:360:240:64:96');
  assert.equal(entryText({ bar: 1, start: 5, length: 7, pitch: 1, velocity: 2 }, 480), '1:5:7:1:2');
});

test('quotes and the white space around them are kept when a note changes', () => {
  assert.equal(format(setVelocity(parse(' "1:0:1:60:96\n  1:1:1:64:96" '), 1, 10)), ' "1:0:1:60:96\n  1:1:1:64:10" ');
});

test('absolute times count from the first bar', () => {
  const rows = absolute(parse('1:0:1:60:96 3:1:1:64:96', 480));
  assert.deepEqual(rows.map((r) => r.at), [0, 2 * 4 * 480 + 1]);
});

test('updateNote refuses a note that is not there', () => {
  assert.throws(() => updateNote(parse('1:0:1:60:96'), 3, { pitch: 1 }), RangeError);
});

test('pasting places each note in the bar it starts in and shortens it at the bar line', () => {
  const model = parse('1:0:1:60:96 1:3:1:64:90', 1_000_000);
  const copied = absolute(model).map(({ at, length, pitch, velocity }) => ({ at, length, pitch, velocity }));
  const next = pasteNotes(model, copied, 2_000_000, 2);
  assert.equal(format(next), '1:0:1:60:96 1:3:1:64:90 1:2:1:62:96 2:1:1:66:90');
  const crossing = pasteNotes(parse(''), [{ at: 3_500_000, length: 1_000_000, pitch: 60, velocity: 96 }], 0, 0);
  assert.equal(format(crossing), '1:3.5:0.5:60:96');
  assert.equal(format(pasteNotes(parse(''), [{ at: 100, length: 5, pitch: 1, velocity: 2, channel: 3, bank: 0, program: 4 }], 0, 0)).startsWith('1:0.0001:'), true);
});

test('piano-roll hum import converts parsed note entries to absolute note values before pasting', () => {
  const model = parse('1:0.25:0.25:52:105', 1_000_000);
  const found = absolute(parse('1:2.25:0.25:50:105 1:3:0.5:48:90', model.ppq))
    .map(({ at, length, pitch, velocity, channel, bank, program }) => ({ at, length, pitch, velocity, channel, bank, program }));
  const imported = format(pasteNotes(model, found, 0, 0));
  assert.equal(imported, '1:0.25:0.25:52:105 1:2.25:0.25:50:105 1:3:0.5:48:90');
  assert.doesNotMatch(imported, /NaN|undefined/);
});

test('played notes become notes: times from milliseconds and the tempo, snapped to a grid when asked', () => {
  const played = [{ on: 510, off: 1020, pitch: 64, velocity: 100 }, { on: 0, off: 480, pitch: 60, velocity: 90 }];
  const raw = recordedToNotes(played, 120, 480, 0, 0);
  assert.deepEqual(raw.map((n) => [n.at, n.length, n.pitch]), [[0, 461, 60], [490, 489, 64]]);    // 120 bpm: a beat is 500 ms and 480 ticks
  const snapped = recordedToNotes(played, 120, 480, 0, 120);
  assert.deepEqual(snapped.map((n) => [n.at, n.length]), [[0, 480], [480, 480]]);
  assert.equal(recordedToNotes([{ on: 0, off: 1, pitch: 60, velocity: 300 }], 120, 480, 960, 120)[0].velocity, 127);
  assert.equal(recordedToNotes([{ on: 0, off: 1, pitch: 60, velocity: 90 }], 120, 480, 960, 120)[0].at, 960);
});

test('the computer keyboard plays a piano from a base octave', () => {
  assert.equal(keyToPitch('a', 4), 60);
  assert.equal(keyToPitch('w', 4), 61);
  assert.equal(keyToPitch('k', 4), 72);
  assert.equal(keyToPitch('q', 4), null);
  assert.equal(keyToPitch('l', 9), 127);
});


// ---- G08: performance and compatibility ----

test('a hundred thousand notes read, change and write quickly, and one change touches one entry', () => {
  const entries = Array.from({ length: 100_000 }, (_, i) => `${(i % 5000) + 1}:${(i % 4) }:0.5:${40 + (i % 40)}:${60 + (i % 60)}`);
  const text = `"${entries.join(' ')}"`;
  const t0 = performance.now();
  const model = parse(text);
  assert.equal(model.notes.length, 100_000);
  assert.equal(model.representable, true);
  assert.equal(format(model), text);
  const moved = moveNotes(model, [0, 50_000, 99_999], 250_000, 1);
  const out = format(moved);
  const elapsed = performance.now() - t0;
  assert.ok(elapsed < 4000, `took ${Math.round(elapsed)} ms`);
  const before = text.split(' '), after = out.split(' ');
  assert.equal(after.length, before.length);
  assert.equal(after.filter((x, i) => x !== before[i]).length, 3);
  assert.throws(() => pasteNotes(model, [{ at: 0, length: 1, pitch: 60, velocity: 90 }]), RangeError);
});

test('every compact notes value in the examples and the conformance files reads back byte for byte', () => {
  const here = path.dirname(fileURLToPath(import.meta.url));
  const base = path.resolve(here, '../../..');
  const files = [];
  const walk = (dir) => { for (const e of fs.readdirSync(dir, { withFileTypes: true })) { const p = path.join(dir, e.name); if (e.isDirectory()) { if (!['node_modules', '.ample', 'bin', 'obj'].includes(e.name)) walk(p); } else if (e.name.endsWith('.amplet')) files.push(p); } };
  for (const dir of ['docs/examples', 'spec-tests']) if (fs.existsSync(path.join(base, dir))) walk(path.join(base, dir));
  let seen = 0;
  for (const file of files) {
    for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
      const m = /^\s+notes\s+(.*\S)\s*$/.exec(line);
      if (!m || m[1].startsWith('{')) continue;
      seen++;
      assert.equal(format(parse(m[1])), m[1], `${path.basename(file)}: ${m[1].slice(0, 60)}`);
    }
  }
  assert.ok(seen >= 1, 'expected at least one notes value in the examples');
});

// PR06: the per-note extension suffix (;pan=..;fine=..;rel=..;cut=..;slide=..;port=..), a default value never written, unknown keys kept verbatim.
test('PR06: round trip for every extension key, in and out of range, in any order, with unknown keys kept', () => {
  const cases = [
    '1:0:1:60:96;pan=-40', '1:0:1:60:96;fine=15', '1:0:1:60:96;rel=300', '1:0:1:60:96;cut=70', '1:0:1:60:96;slide=1', '1:0:1:60:96;port=120',
    '1:0:1:60:96;pan=-40;fine=15;rel=300;cut=70;slide=1;port=120',
    '1:0:1:60:96:0:0:5:7;pan=100;cut=0',
    '1:0:1:60:96;grp=3', '1:0:1:60:96;pan=10;grp=3;mystery=42',
  ];
  for (const text of cases) assert.equal(format(parse(text)), text, text);
});

test('PR06: a value equal to the default is never written back', () => {
  const n = parseEntry('1:0:1:60:96', 1_000_000);
  assert.equal(entryText({ ...n, pan: 0, fine: 0, cut: 100, slide: 0, port: 0 }, 1_000_000), '1:0:1:60:96');
  assert.equal(entryText({ ...n, rel: undefined }, 1_000_000), '1:0:1:60:96');
});

test('PR06: a known key out of range makes the whole entry raw (not repaired)', () => {
  for (const bad of ['1:0:1:60:96;pan=101', '1:0:1:60:96;pan=-101', '1:0:1:60:96;cut=101', '1:0:1:60:96;slide=2', '1:0:1:60:96;rel=5001', '1:0:1:60:96;port=2001', '1:0:1:60:96;pan=abc', '1:0:1:60:96;pan']) {
    assert.equal(parseEntry(bad, 1_000_000), null, bad);
    const model = parse(bad, 1_000_000);
    assert.equal(model.notes.length, 0);
    assert.equal(format(model), bad);   // kept raw, byte for byte
  }
});

test('PR06: a duplicate known key makes the whole entry raw', () => {
  assert.equal(parseEntry('1:0:1:60:96;pan=10;pan=20', 1_000_000), null);
});
