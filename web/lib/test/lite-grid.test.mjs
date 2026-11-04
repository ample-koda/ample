import test from 'node:test';
import assert from 'node:assert/strict';
import { gridPitches, GM_DRUM_DEFAULTS, stepTicks, columnOf, cellRows, cellShade, rowAt, pitchLabel, noteLabel, drumChoices } from '../lite-grid.mjs';
import { mergedRows, drawRow, removeRows, velocityRows, adjustRows, quantizeRows } from '../lite-gestures.mjs';
import { parse, format } from '../notes-model.mjs';

const PPQ = 1000, BAR = 4 * PPQ, END = 4 * BAR, mk = (text) => parse(text, PPQ);

test('rows are the distinct pitches present, highest first, plus pinned rows', () => {
  assert.deepEqual(gridPitches([36, 38, 36, 42]), [42, 38, 36]);
  assert.deepEqual(gridPitches([36], [36, 46, 38]), [46, 38, 36]);
  assert.deepEqual(gridPitches([], [40]), [40]);
  assert.deepEqual(gridPitches([200, -1, 1.5, NaN]), GM_DRUM_DEFAULTS.slice().sort((a, b) => b - a));
});

test('an empty clip gets the General MIDI drum defaults, highest row first, with drum names', () => {
  const rows = gridPitches([]);
  assert.deepEqual(rows, [...rows].sort((a, b) => b - a));
  for (const p of [36, 38, 42, 46, 39]) assert.ok(rows.includes(p), `pitch ${p}`);
  assert.equal(pitchLabel(36), 'Kick'); assert.equal(pitchLabel(38), 'Snare'); assert.equal(pitchLabel(42), 'Closed hat');
  assert.equal(pitchLabel(1), 'C#-1'); assert.equal(noteLabel(60), 'C4');
  assert.ok(drumChoices().every((c, i, a) => !i || a[i - 1].pitch > c.pitch));
});

test('the step follows the snap and is a 1/16 when snap is off or free', () => {
  assert.equal(stepTicks(0.25, PPQ), 250);
  assert.equal(stepTicks(0.5, PPQ), 500);
  assert.equal(stepTicks(0, PPQ), 250);
  assert.equal(stepTicks(undefined, PPQ), 250);
  assert.equal(stepTicks(1 / 3, PPQ), 333);
  assert.equal(columnOf(0, 250), 0); assert.equal(columnOf(249, 250), 0); assert.equal(columnOf(250, 250), 1); assert.equal(columnOf(-5, 250), 0);
});

test('a cell holds the notes that start inside it, at its pitch only', () => {
  const m = mk('1:0:250:36:96 1:250:250:36:80 1:300:100:38:90 1:1000:250:42:100'), rows = mergedRows(m);
  assert.equal(cellRows(rows, 0, 36, 250).length, 1);
  assert.equal(cellRows(rows, 1, 36, 250).length, 1);
  assert.equal(cellRows(rows, 1, 38, 250).length, 1);   // an off-grid note shows in the cell it starts in
  assert.equal(cellRows(rows, 0, 38, 250).length, 0);
  assert.equal(cellRows(rows, 4, 42, 250).length, 1);
  assert.equal(cellRows(rows, 5, 42, 250).length, 0);
});

test('toggling a cell writes and removes one-step notes through the Roll view calls, and the round trip is exact', () => {
  let m = mk('');
  m = drawRow(m, 0, 36, 250, END, 96); m = drawRow(m, 500, 36, 250, END, 127); m = drawRow(m, 250, 42, 250, END, 96);
  assert.equal(mergedRows(m).length, 3);
  assert.equal(drawRow(m, 0, 36, 250, END, 96), m);   // an occupied cell never gets a duplicate
  const before = format(m);
  const cell = cellRows(mergedRows(m), 2, 36, 250);
  assert.equal(cell[0].velocity, 127);
  const off = removeRows(m, cell);
  assert.equal(mergedRows(off).length, 2);
  assert.equal(format(drawRow(off, 500, 36, 250, END, 127)).split(' ').sort().join(' '), before.split(' ').sort().join(' '));
  assert.equal(drawRow(m, END, 36, 250, END), m);   // past the clip end nothing is drawn
});

test('accent, wheel and quick quantize reuse the Roll view maths on a cell', () => {
  let m = mk('1:0:250:36:96 1:500:250:38:90');
  const cell = cellRows(mergedRows(m), 0, 36, 250);
  m = velocityRows(m, cell, 127);
  assert.equal(mergedRows(m)[0].velocity, 127);
  const wheel = adjustRows(m, cellRows(mergedRows(m), 2, 38, 250), (n) => ({ velocity: n.velocity + 5 }));
  assert.equal(mergedRows(wheel).find((r) => r.pitch === 38).velocity, 95);
  const off = mk('1:100:250:36:96');
  const q = quantizeRows(off, mergedRows(off), 250, END);
  assert.equal(mergedRows(q)[0].at, 0);
  assert.equal(format(quantizeRows(q, mergedRows(q), 250, END)), format(q));
});

test('velocity shading is monotonic, bounded and clamps', () => {
  let last = 0;
  for (const v of [1, 20, 40, 64, 96, 127]) { const s = cellShade(v); assert.ok(s > last && s >= 0.3 && s <= 1, String(v)); last = s; }
  assert.equal(cellShade(127), 1); assert.equal(cellShade(500), 1); assert.equal(cellShade(-4), 0.3);
  assert.equal(cellShade(64), 0.653);
});

test('rowAt maps a pixel to a row or -1', () => {
  assert.equal(rowAt(0, 0, 24, 3), 0); assert.equal(rowAt(50, 0, 24, 3), 2); assert.equal(rowAt(72, 0, 24, 3), -1);
  assert.equal(rowAt(-1, 0, 24, 3), -1); assert.equal(rowAt(5, 24, 24, 3), 1);
});

import { coveringRows, cellInfo, zoomGridRows } from '../lite-grid.mjs';
import { serializeView, restoreView, LIMITS } from '../lite-flow.mjs';

test('a note longer than a step covers several cells: a head cell, tail cells and the last one', () => {
  const rows = mergedRows(mk('1:0:2500:36:96'));
  const step = 1000;
  assert.deepEqual([0, 1, 2, 3].map((c) => coveringRows(rows, c, 36, step).length), [1, 1, 1, 0]);
  assert.deepEqual(cellInfo(rows, 0, 36, step), { rows: [rows[0]], head: true, tail: false, last: false });
  assert.deepEqual([cellInfo(rows, 1, 36, step).tail, cellInfo(rows, 1, 36, step).last], [true, false]);
  assert.deepEqual([cellInfo(rows, 2, 36, step).tail, cellInfo(rows, 2, 36, step).last], [true, true]);
  assert.equal(coveringRows(rows, 1, 38, step).length, 0);
  assert.equal(cellInfo(rows, 5, 36, step).rows.length, 0);
  const tied = mergedRows(mk('1:3000:1000:36:96;tie=0 2:0:2000:36:96;tie=0'));
  assert.equal(coveringRows(tied, 5, 36, 1000).length, 1);   // a tied note across a bar line is one covering row
});

test('Grid row zoom stays between 14 and 56 px', () => {
  assert.equal(zoomGridRows(24, 1.15), 27.6);
  assert.equal(zoomGridRows(50, 2), 56);
  assert.equal(zoomGridRows(15, 0.1), 14);
});

test('the view record keeps the Grid row height and the muted rows only when they differ from the default', () => {
  const base = { pxBeat: 60, rowH: 14, scrollX: 0, scrollY: 0, snap: 'auto', lastLen: 0, mode: 'grid' };
  assert.equal('gridRowH' in serializeView(base), false);
  assert.equal('muted' in serializeView({ ...base, muted: [] }), false);
  const rec = serializeView({ ...base, gridRowH: 40.4, muted: [38, 36, 36, 200, 1.5] });
  assert.deepEqual([rec.gridRowH, rec.muted], [40.4, [36, 38]]);
  const back = restoreView(rec);
  assert.deepEqual([back.gridRowH, back.muted], [40.4, [36, 38]]);
  assert.equal(restoreView({ v: 1, gridRowH: 999 }).gridRowH, LIMITS.maxGridRowH);
  assert.equal('muted' in restoreView({ v: 1, muted: ['x', -3] }), false);
  assert.equal('gridRowH' in restoreView({ v: 1, gridRowH: 'big' }), false);
});

import { knobFactor, rowVolume, scaleVelocity } from '../lite-grid.mjs';

test('the volume of a row is the average velocity of its notes', () => {
  const rows = [{ pitch: 36, velocity: 100 }, { pitch: 36, velocity: 60 }, { pitch: 38, velocity: 90 }];
  assert.equal(rowVolume(rows, 36), 80);
  assert.equal(rowVolume(rows, 40), 0);
});

test('dragging the knob scales the velocities inside 1 to 127', () => {
  assert.equal(knobFactor(0), 1);
  assert.equal(Math.round(knobFactor(-90) * 100), 200);
  assert.equal(Math.round(knobFactor(90) * 100), 50);
  assert.equal(knobFactor(-9999), 4);
  assert.equal(knobFactor(9999), 0.1);
  assert.equal(scaleVelocity(100, 2), 127);
  assert.equal(scaleVelocity(100, 0.5), 50);
  assert.equal(scaleVelocity(3, 0.1), 1);
});
