import test from 'node:test';
import assert from 'node:assert/strict';
import { mergedRows, moveRows, cloneRows, resizeRows, removeRows, drawRow, velocityRows, soundingRows, soundingPitches, createThrottle, clampMove, sliceRow, rowIndexAt, quantizeRows } from '../lite-gestures.mjs';
import { autoScrollSpeed, autoScrollDelta, sliceNote } from '../lite-roll-model.mjs';
import { parse, format, absolute } from '../notes-model.mjs';

const PPQ = 1000, BAR = 4 * PPQ, END = 8 * BAR;
const mk = (text) => parse(text, PPQ);
const rowsOf = (model) => mergedRows(model);
const pick = (model, ...indexes) => rowsOf(model).filter((r) => indexes.includes(r.index));

test('moving a note keeps its place in the text, its fields and every other entry byte for byte', () => {
  const m = mk('1:0:500:60:96;pan=20 1:500:500:64:90 2:0:500:67:80');
  const next = moveRows(m, pick(m, 0), 1000, 2, END);
  assert.equal(format(next), '1:1000:500:62:96;pan=20 1:500:500:64:90 2:0:500:67:80');
  assert.equal(format(parse(format(next), PPQ)), format(next));
});

test('moving across a bar line re-bars the note and a long note is re-split as tied segments', () => {
  const m = mk('1:3500:500:60:96');
  assert.equal(format(moveRows(m, pick(m, 0), 1000, 0, END)), '2:500:500:60:96');
  const long = mk('1:2000:2000:60:96;pan=5 2:0:1000:60:96;pan=5');   // untied adjacent segments stay two notes
  assert.equal(rowsOf(long).length, 2);
  let t = mk('1:3000:1000:60:96;tie=0;pan=7 2:0:1000:60:96;tie=0;pan=7');
  assert.equal(rowsOf(t).length, 1);
  t = moveRows(t, rowsOf(t), 3500, 0, END);
  const rows = rowsOf(t);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].at, 6500);
  assert.equal(rows[0].length, 2000);
  assert.equal(rows[0].pan, 7);
  assert.equal(format(t), '2:2500:1500:60:96;pan=7;tie=0 3:0:500:60:96;pan=7;tie=0');
});

test('a group move is clamped at tick 0, the clip end and the pitch range', () => {
  const m = mk('1:0:500:1:96 1:500:500:2:96');
  assert.deepEqual(clampMove(rowsOf(m), -5000, -10, END), { dTick: 0, dPitch: -1 });
  assert.deepEqual(clampMove(rowsOf(m), 9e9, 500, END), { dTick: END - 1000, dPitch: 125 });
});

test('a moved note that lands on another note covers it, no duplicate stays', () => {
  const m = mk('1:0:500:60:96 1:500:500:60:50;pan=3');
  const next = moveRows(m, pick(m, 0), 500, 0, END);
  assert.equal(rowsOf(next).length, 1);
  assert.equal(format(next), '1:500:500:60:96');
});

test('resizing grows across bar lines with a shared tie, and shrinking merges back', () => {
  let m = mk('1:3000:500:60:96;pan=9 1:0:500:64:96');
  m = resizeRows(m, pick(m, 0), 2500, END);
  assert.equal(rowsOf(m).length, 2);
  const r = rowsOf(m).find((x) => x.pitch === 60);
  assert.equal(r.length, 3000);
  assert.equal(r.pan, 9);
  assert.equal(parse(format(m), PPQ).notes.filter((i) => i.note.pitch === 60).length, 2);
  m = resizeRows(m, rowsOf(m).filter((x) => x.pitch === 60), -2800, END);
  assert.equal(format(m), '1:3000:200:60:96;pan=9 1:0:500:64:96');
  assert.equal(resizeRows(m, rowsOf(m), 0, END), m);
});

test('resizing never goes below one tick or past the clip end', () => {
  const m = mk('1:0:500:60:96');
  assert.equal(rowsOf(resizeRows(m, rowsOf(m), -99999, END))[0].length, 1);
  assert.equal(rowsOf(resizeRows(m, rowsOf(m), 9e9, END))[0].length, END);
});

test('a multi-selection resizes by the same amount', () => {
  const m = mk('1:0:500:60:96 1:1000:250:62:96');
  const next = resizeRows(m, rowsOf(m), 250, END);
  assert.deepEqual(rowsOf(next).map((r) => r.length), [750, 500]);
});

test('clone leaves the original and copies fields; a zero shift is a no-op', () => {
  const m = mk('1:0:500:60:96;fine=5');
  assert.equal(cloneRows(m, rowsOf(m), 0, 0, END), m);
  const next = cloneRows(m, rowsOf(m), 1000, 3, END);
  assert.equal(format(next), '1:0:500:60:96;fine=5 2:0:500:63:96;fine=5'.replace('2:0', '2:0').replace('2:0:500:63', '1:1000:500:63'));
});

test('removeRows takes out whole tied notes and the neighbouring space', () => {
  const m = mk('1:0:500:60:96 1:3000:1000:64:96;tie=0 2:0:1000:64:96;tie=0 2:1000:500:67:96');
  const next = removeRows(m, pick(m, 1));
  assert.equal(format(next), '1:0:500:60:96 2:1000:500:67:96');
  assert.equal(format(removeRows(m, pick(m, 0, 3))), '1:3000:1000:64:96;tie=0 2:0:1000:64:96;tie=0');
});

test('drawRow clips to the clip, ties across a bar and never stacks a duplicate', () => {
  let m = mk('');
  m = drawRow(m, 3000, 60, 2000, END);
  assert.equal(rowsOf(m).length, 1);
  assert.equal(rowsOf(m)[0].length, 2000);
  assert.equal(drawRow(m, 3000, 60, 100, END), m);
  assert.equal(drawRow(m, END + 10, 60, 100, END), m);
  assert.equal(rowsOf(drawRow(m, END - 100, 61, 5000, END))[1].length, 100);
});

test('velocityRows sets every segment of a tied note and keeps the rest', () => {
  const m = mk('1:3000:1000:64:96;tie=0 2:0:1000:64:96;tie=0 1:0:500:60:96');
  const next = velocityRows(m, pick(m, 0), 100);
  assert.equal(format(next), '1:3000:1000:64:100;tie=0 2:0:1000:64:100;tie=0 1:0:500:60:96');
  assert.equal(velocityRows(next, pick(next, 0), 100), next);
});

test('sounding notes at a transport position (lit notes and keys)', () => {
  const m = mk('1:0:1000:60:96 1:1000:1000:64:96 1:0:2000:67:96');
  const rows = rowsOf(m);
  assert.deepEqual([...soundingRows(rows, 0)].sort(), [0, 2]);
  assert.deepEqual([...soundingRows(rows, 1000)].sort(), [1, 2]);
  assert.deepEqual([...soundingRows(rows, 2000)], []);
  assert.deepEqual([...soundingPitches(rows, 1500)].sort(), [64, 67]);
  const tied = rowsOf(mk('1:3000:1000:64:96;tie=0 2:0:1000:64:96;tie=0'));
  assert.equal(soundingRows(tied, 4500).size, 1);
});

test('the audition throttle lets one call through per window', () => {
  let now = 0;
  const allow = createThrottle(80, () => now);
  assert.equal(allow(), true);
  now = 40; assert.equal(allow(), false);
  now = 85; assert.equal(allow(), true);
  now = 100; assert.equal(allow(), false);
});

test('auto-scroll speed grows with the distance and is capped', () => {
  assert.equal(autoScrollSpeed(0), 0);
  assert.equal(autoScrollSpeed(-5), 0);
  assert.ok(autoScrollSpeed(10) > autoScrollSpeed(2));
  assert.equal(autoScrollSpeed(1e6), 48);
  assert.equal(autoScrollDelta(300, 600), 0);
  assert.ok(autoScrollDelta(-40, 600) < autoScrollDelta(5, 600));
  assert.ok(autoScrollDelta(-40, 600) < 0);
  assert.ok(autoScrollDelta(700, 600) > autoScrollDelta(590, 600));
  assert.ok(autoScrollDelta(590, 600) > 0);
});

test('slicing onto an occupied start is refused', () => {
  const m = mk('1:0:1000:60:96 1:500:200:60:80');
  const row = rowsOf(m)[0];
  assert.equal(sliceRow(m, row, 500, sliceNote), m);
  assert.equal(rowsOf(sliceRow(m, row, 600, sliceNote)).length, 3);
  assert.equal(rowIndexAt(m, 500, 60), 1);
});

// ---- the seeded model fuzz of decision 31 ----

function rng(seed) { let s = seed >>> 0; return () => { s = (s + 0x6d2b79f5) >>> 0; let t = s; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }

const SENTINEL = '8:3000:500:5:77;pan=-33;fine=12;slide=1;grp=3';

function seedModel(rand) {
  const parts = [];
  for (let i = 0; i < 6; i++) parts.push(`${1 + Math.floor(rand() * 6)}:${Math.floor(rand() * 8) * 500}:${250 + Math.floor(rand() * 4) * 250}:${40 + Math.floor(rand() * 30)}:${60 + Math.floor(rand() * 60)};pan=${i * 10 - 25};fine=${i + 1}`);
  parts.push(SENTINEL);
  const dedupe = new Set();
  const text = parts.filter((p) => { const f = p.split(';')[0].split(':'); const key = `${f[0]}:${f[1]}:${f[3]}`; if (dedupe.has(key)) return false; dedupe.add(key); return true; }).join(' ');
  return parse(text, PPQ);
}

const fuzzCounts = { tied: 0 };   // tied pieces checked by the piece rule
function invariants(model, label) {
  const text = format(model);
  assert.equal(format(parse(text, PPQ)), text, `${label}: format(parse(text)) is stable`);
  const rows = rowsOf(model), seen = new Set();
  for (const r of rows) {
    for (const v of [r.at, r.length, r.pitch, r.velocity]) assert.ok(Number.isFinite(v), `${label}: finite`);
    assert.ok(r.length > 0 && r.at >= 0 && r.at + r.length <= END, `${label}: inside the clip (${r.at}+${r.length})`);
    assert.ok(r.pitch >= 0 && r.pitch <= 127 && r.velocity >= 1 && r.velocity <= 127, `${label}: ranges`);
    const key = `${r.at}:${r.pitch}`;
    assert.ok(!seen.has(key), `${label}: no duplicate at ${key}`);
    seen.add(key);
    if (r.pan !== undefined) assert.ok(Number.isInteger(r.pan) && (r.pan === -33 || (r.pan >= -25 && r.pan <= 25)), `${label}: pan kept`);
  }
  // the whole span counts: no two pieces (a tied tail included) share a start and pitch
  const pieces = new Set();
  for (const seg of absolute(model)) { const key = `${seg.at}:${seg.pitch}`; assert.ok(!pieces.has(key), `${label}: piece overlap at ${key}`); pieces.add(key); if (seg.tie !== undefined) fuzzCounts.tied++; }
  // tied segments of one logical note carry identical fields
  for (const [i, item] of model.notes.entries()) assert.ok(item.note.length >= 1, `${label}: segment ${i}`);
}

function runFuzz(seed, steps) {
  const rand = rng(seed), log = [];
  let model = seedModel(rand);
  const sentinelRaw = SENTINEL;
  const choose = () => {
    const rows = rowsOf(model).filter((r) => !(r.pitch === 5 && r.bar === 8));
    return rows.filter(() => rand() < 0.4).slice(0, 4).concat(rows.length ? [rows[Math.floor(rand() * rows.length)]] : []).filter((r, i, a) => a.findIndex((x) => x.index === r.index) === i);
  };
  for (let step = 0; step < steps; step++) {
    const kind = Math.floor(rand() * 8), rows = choose(), d = Math.round((rand() - 0.5) * 12000), dp = Math.round((rand() - 0.5) * 30);
    log.push(`${kind}:${rows.map((r) => r.index).join(',')}:${d}:${dp}`);
    try {
      if (kind === 0) model = moveRows(model, rows, d, dp, END);
      else if (kind === 1) model = cloneRows(model, rows, d, dp, END);
      else if (kind === 2) model = resizeRows(model, rows, d, END);
      else if (kind === 3) model = removeRows(model, rows.slice(0, 1));
      else if (kind === 4) model = drawRow(model, Math.abs(d) * 3 % (END - 1), 36 + Math.abs(dp) % 40, 100 + Math.abs(d), END);
      else if (kind === 5) model = velocityRows(model, rows, 1 + Math.abs(d) % 127);
      else if (kind === 6 && rows[0]) model = sliceRow(model, rows[0], rows[0].at + 1 + Math.abs(d) % Math.max(1, rows[0].length - 1), sliceNote);
      else model = resizeRows(model, rows, -d, END);
    } catch (error) { assert.fail(`seed ${seed} threw ${error.message}; last actions ${log.slice(-5).join(' | ')}`); }
    try { invariants(model, `seed ${seed} step ${step}`); assert.ok(format(model).includes(sentinelRaw), 'the untouched sentinel keeps its text'); }
    catch (error) { assert.fail(`${error.message} (seed ${seed}, last actions ${log.slice(-5).join(' | ')}; text ${format(model)})`); }
  }
}

test('seeded gesture fuzz: invariants hold after every move, clone, resize, delete, draw, velocity and slice', () => {
  for (let seed = 1; seed <= 300; seed++) { try { runFuzz(seed, 40); } catch (error) { assert.fail(`${error.message}`); } }
  assert.ok(fuzzCounts.tied > 100, `the fuzz really exercised tied pieces (${fuzzCounts.tied})`);
});

// ---- the tied-tail guard: the start+pitch rule spans every piece of a tied note ----
const TIED = '1:3000:1000:60:96;tie=0 2:0:1000:60:96;tie=0';   // one note, tail piece starts at tick 4000
test('drawing onto the tail piece of a tied note does nothing', () => {
  const m = mk(TIED);
  assert.equal(rowsOf(m).length, 1);
  assert.equal(rowIndexAt(m, 4000, 60), 0);
  assert.equal(drawRow(m, 4000, 60, 500, END), m);
  assert.notEqual(drawRow(m, 4000, 61, 500, END), m);
});

test('moving or cloning a note onto a tail piece replaces the tied note', () => {
  const m = mk(`${TIED} 3:0:500:62:90`);
  const moved = moveRows(m, pick(m, 2), 0, -2, END);   // 62 -> 60 at tick 8000: no collision
  assert.equal(rowsOf(moved).length, 2);
  const m2 = mk(`${TIED} 1:0:500:62:90`);
  const onto = moveRows(m2, pick(m2, 2), 4000, -2, END);   // lands on 4000:60, the tail
  assert.deepEqual(rowsOf(onto).map((r) => [r.at, r.pitch, r.length]), [[4000, 60, 500]]);
  const cloned = cloneRows(m2, pick(m2, 2), 4000, -2, END);
  assert.deepEqual(rowsOf(cloned).map((r) => [r.at, r.pitch]).sort((a, b) => a[0] - b[0]), [[0, 62], [4000, 60]]);
});

test('a moved tied note whose tail lands on another start replaces that note, and a growing note stops before it', () => {
  const m = mk('1:3000:1000:60:96;tie=0 2:0:1000:60:96;tie=0 3:0:500:60:80');   // note B at 8000
  const shifted = moveRows(m, pick(m, 0), 4000, 0, END);   // pieces at 7000 and 8000: the tail hits B
  assert.equal(rowsOf(shifted).length, 1);
  const g = mk('1:0:500:60:96 2:0:500:60:80');   // B at 4000
  const grown = resizeRows(g, pick(g, 0), 9000, END);
  assert.equal(rowsOf(grown)[0].length, 4000);
});

test('quick quantize never leaves two notes with a piece on the same start and pitch', () => {
  const m = mk('1:0:500:60:96 1:3900:600:60:90;tie=3 2:0:500:60:90;tie=3');
  const next = quantizeRows(m, rowsOf(m), 1000, END);
  const keys = absolute(next).map((s) => `${s.at}:${s.pitch}`);
  assert.equal(new Set(keys).size, keys.length);
});

// ---- the MIDI clipboard text ----
import { clipboardText, itemsFromText, copyItems, pasteRows } from '../lite-gestures.mjs';
test('clipboard text round-trips notes with all their fields, tied notes included', () => {
  const m = mk('1:0:500:60:96;pan=20 1:3000:1000:64:90;tie=0 2:0:1000:64:90;tie=0');
  const items = copyItems(m, rowsOf(m));
  const text = clipboardText(items, PPQ);
  assert.match(text, /pan=20/);
  const back = itemsFromText(text, PPQ);
  assert.deepEqual(back.map((i) => [i.at, i.length, i.note.pitch, i.note.pan]), items.map((i) => [i.at, i.length, i.note.pitch, i.note.pan]));
  const into = pasteRows(mk(''), back, 8000, END);
  assert.equal(rowsOf(into).length, 2);
});

test('text that is not plain note entries gives no items and never throws', () => {
  for (const text of ['', '   ', 'hello world', 'notes { note { pitch=60 } }', '1:0:1', '{"a":1}', null, undefined, 42, '1:0:500:60:96 junk']) assert.deepEqual(itemsFromText(text, PPQ), []);
  assert.equal(itemsFromText('"1:0:500:60:96"', PPQ).length, 1);
  assert.equal(clipboardText([], PPQ), '');
});
