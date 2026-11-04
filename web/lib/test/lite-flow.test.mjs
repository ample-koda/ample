import test from 'node:test';
import assert from 'node:assert/strict';
import { zoomAnchor, centerOnNotes, zoomPreset, fitRange, pasteTick, serializeView, restoreView, readViews, writeViews, defaultView, selectionKeys, keysToIndexes, LIMITS } from '../lite-flow.mjs';
import { mergedRows, quantizeRows, copyItems, pasteRows, duplicateRows, adjustRows, removeRows } from '../lite-gestures.mjs';
import { sliceNote } from '../lite-roll-model.mjs';
import { parse, format } from '../notes-model.mjs';

const PPQ = 1000, BAR = 4 * PPQ, END = 8 * BAR;
const mk = (text) => parse(text, PPQ);
const rowsOf = (m) => mergedRows(m);

test('zoomAnchor keeps the beat and the row under the pointer fixed', () => {
  const view = { pxBeat: 60, rowH: 14, scrollX: 300, scrollY: 400 }, pointer = { x: 200, y: 90 };
  const beat = (pointer.x + view.scrollX) / view.pxBeat, row = (pointer.y + view.scrollY) / view.rowH;
  for (const factor of [1.15, 0.87, 2, 0.5]) {
    const next = zoomAnchor(view, pointer, factor, 'both');
    assert.ok(Math.abs((pointer.x + next.scrollX) / next.pxBeat - beat) < 1e-9);
    assert.ok(Math.abs((pointer.y + next.scrollY) / next.rowH - row) < 1e-9);
  }
  assert.equal(zoomAnchor(view, pointer, 2, 'time').rowH, 14);
  assert.equal(zoomAnchor(view, pointer, 2, 'rows').pxBeat, 60);
});

test('zoomAnchor clamps the zoom and never scrolls below zero', () => {
  const view = { pxBeat: 60, rowH: 14, scrollX: 0, scrollY: 0 };
  assert.equal(zoomAnchor(view, { x: 0, y: 0 }, 1e6, 'both').pxBeat, LIMITS.maxPxBeat);
  assert.equal(zoomAnchor(view, { x: 500, y: 0 }, 0.001, 'time').scrollX, 0);
  assert.equal(zoomAnchor({ ...view, scrollX: 100 }, { x: 50, y: 0 }, 2, 'time', { x: 120 }).scrollX, 120);
});

test('centerOnNotes centres the pitch range and an empty clip on C4-C5, inside the scroll range', () => {
  const rowH = 14, viewH = 280;
  const y = centerOnNotes([{ pitch: 60 }, { pitch: 72 }], rowH, viewH);
  assert.equal(y, centerOnNotes([], rowH, viewH));
  const middle = (127 - 66) * rowH + rowH / 2;
  assert.ok(Math.abs(y + viewH / 2 - middle) <= 1);
  assert.equal(centerOnNotes([{ pitch: 0 }], rowH, viewH), 128 * rowH - viewH);
  assert.equal(centerOnNotes([{ pitch: 127 }], rowH, viewH), 0);
});

test('zoom presets, fitRange and the paste position rule', () => {
  assert.deepEqual([1, 2, 3, 9, 0].map(zoomPreset), [24, 60, 140, 140, 24]);
  const fit = fitRange(0, 8 * PPQ, PPQ, 424);
  assert.equal(fit.pxBeat, 47);
  assert.equal(fit.scrollX, 0);
  assert.ok(fitRange(4 * PPQ, 6 * PPQ, PPQ, 424).scrollX > 0);
  assert.equal(pasteTick(2300, 9000, (t) => Math.round(t / 1000) * 1000), 2000);
  assert.equal(pasteTick(null, 9000), 9000);
  assert.equal(pasteTick(undefined, -5), 0);
});

test('view memory round trips and survives garbage', () => {
  const s = { panel: { left: 40.4, top: 60, width: 900, height: 520 }, bracket: { start: 1000, end: 5000 }, loopOn: true, follow: false, snap: '1/8', pxBeat: 88.333, rowH: 16, scrollX: 123.6, scrollY: 400, lastLen: 500, hintDismissed: true };
  const stored = JSON.parse(JSON.stringify(serializeView(s)));
  const back = restoreView(stored, { endTick: END, snaps: ['auto', '1/8'] });
  assert.deepEqual(back, { panel: { left: 40, top: 60, width: 900, height: 520 }, bracket: { start: 1000, end: 5000 }, loopOn: true, follow: false, snap: '1/8', pxBeat: 88.33, rowH: 16, scrollX: 124, scrollY: 400, lastLen: 500, hintDismissed: true, mode: 'roll' });
  assert.equal(restoreView(serializeView({ ...s, mode: 'grid' }), { endTick: END, snaps: ['1/8'] }).mode, 'grid');
  assert.equal(restoreView(serializeView({ ...s, mode: 'weird' }), { endTick: END, snaps: ['1/8'] }).mode, 'roll');
  assert.equal('mode' in restoreView({ v: 1, mode: 'weird' }), false);   // an old record without a mode, or a bad one, drops the field
  assert.equal('mode' in restoreView({ v: 1, pxBeat: 50 }), false);
  assert.deepEqual(restoreView(serializeView({ ...s, panel: null, bracket: null }), { endTick: END, snaps: ['1/8'] }).bracket, undefined);
  for (const bad of [null, 7, 'x', [], {}, { v: 2 }, { v: 1, pxBeat: NaN, scrollX: -5, bracket: { start: 5, end: 1 }, snap: 'weird', panel: { left: 1, top: 1, width: 3, height: 3 } }]) {
    const out = restoreView(bad, { endTick: END, snaps: ['auto'] });
    assert.ok(!('bracket' in out) && !('snap' in out) && !('panel' in out) && !('pxBeat' in out));
  }
  assert.equal(restoreView({ v: 1, bracket: { start: 0, end: END + 1 } }, { endTick: END }).bracket, undefined);
  assert.equal(restoreView({ v: 1, pxBeat: 1e9, scrollX: -3 }).pxBeat, LIMITS.maxPxBeat);
  assert.equal(restoreView({ v: 1, scrollX: -3 }).scrollX, 0);
});

test('the per-block view record and the default view', () => {
  assert.deepEqual(readViews('not json'), {});
  assert.deepEqual(readViews('[1]'), {});
  assert.equal(defaultView(undefined), 'lite');
  assert.equal(defaultView({}), 'lite');
  assert.equal(defaultView({ view: 'advanced' }), 'advanced');
  let text = writeViews('', 'a', { view: 'advanced' });
  text = writeViews(text, 'a', { lite: { v: 1 } });
  text = writeViews(text, 'b', { view: 'lite' });
  assert.deepEqual(readViews(text), { a: { view: 'advanced', lite: { v: 1 } }, b: { view: 'lite' } });
  assert.deepEqual(readViews('{"a":{"view":"nonsense"}}'), { a: {} });
});

test('selection keys carry a selection between editors', () => {
  const m = mk('1:0:500:60:96 1:500:500:64:90 2:0:500:67:80');
  const keys = selectionKeys(rowsOf(m).filter((r) => r.index !== 1));
  assert.deepEqual(keysToIndexes(rowsOf(m), keys), [0, 2]);
  assert.deepEqual(keysToIndexes(rowsOf(m), ['9:9']), []);
});

test('quick quantize snaps starts, keeps extension fields and is idempotent', () => {
  const m = mk('1:130:200:60:96;pan=7 1:900:200:64:90 2:0:300:67:80');
  const once = quantizeRows(m, rowsOf(m), 250, END);
  assert.equal(format(once), '1:250:200:60:96;pan=7 1:1000:200:64:90 2:0:300:67:80');
  assert.equal(format(quantizeRows(once, rowsOf(once), 250, END)), format(once));
  assert.equal(quantizeRows(m, [], 250, END), m);
  assert.equal(quantizeRows(m, rowsOf(m), 0, END), m);
});

test('quick quantize moves a tied note as a whole and never leaves a duplicate', () => {
  const t = mk('1:3100:900:60:96;tie=0;pan=3 2:0:1000:60:96;tie=0;pan=3');
  const q = quantizeRows(t, rowsOf(t), 500, END);
  assert.equal(rowsOf(q).length, 1);
  assert.equal(rowsOf(q)[0].at, 3000);
  assert.equal(rowsOf(q)[0].length, 1900);
  assert.equal(rowsOf(q)[0].pan, 3);
  assert.equal(format(quantizeRows(q, rowsOf(q), 500, END)), format(q));
  const d = mk('1:0:200:60:96 1:90:200:60:90');
  const dq = quantizeRows(d, rowsOf(d), 500, END);
  assert.equal(rowsOf(dq).length, 1);
  assert.equal(format(quantizeRows(dq, rowsOf(dq), 500, END)), format(dq));
});

test('quick quantize leaves a note that would end past the clip end where it is', () => {
  const m = mk('1:3900:100:60:96');
  assert.equal(format(quantizeRows(m, rowsOf(m), 1000, 4000)), format(m));
});

test('copy and paste keep every field, land at the tick, tie across bars and replace what they cover', () => {
  const m = mk('1:0:500:60:96;pan=20;fine=5 1:500:500:64:90 3:0:500:67:80');
  const items = copyItems(m, rowsOf(m).filter((r) => r.index < 2));
  assert.equal(items[0].note.pan, 20);
  const pasted = pasteRows(m, items, 2000, END);
  assert.equal(format(pasted), '1:0:500:60:96;pan=20;fine=5 1:500:500:64:90 3:0:500:67:80 1:2000:500:60:96;pan=20;fine=5 1:2500:500:64:90');
  const onto = pasteRows(m, items, 8000, END);
  assert.equal(rowsOf(onto).length, 5);
  assert.equal(format(pasteRows(m, [], 0, END)), format(m));
  // a paste over an existing note at the same start and pitch replaces it, no duplicate
  const same = pasteRows(m, copyItems(m, [rowsOf(m)[0]]), 8000, END);
  const again = pasteRows(same, copyItems(m, [rowsOf(m)[0]]), 8000, END);
  assert.equal(rowsOf(again).length, rowsOf(same).length);
});

test('paste clamps inside the clip and ties a long note across the bar line', () => {
  const m = mk('1:0:2000:60:96');
  const items = copyItems(m, rowsOf(m));
  const p = pasteRows(m, items, 3000, END);
  const rows = rowsOf(p);
  assert.equal(rows.length, 2);
  assert.equal(rows[1].at, 3000);
  assert.equal(rows[1].length, 2000);
  assert.equal(rows[1].tie !== undefined, true);
  assert.equal(rowsOf(pasteRows(m, items, END * 5, END))[1].at, END - 2000);
});

test('Ctrl+B duplicates the selection right behind itself, unless it would pass the clip end', () => {
  const m = mk('1:0:500:60:96;slide=1 1:500:500:64:90');
  const d = duplicateRows(m, rowsOf(m), END);
  assert.deepEqual(rowsOf(d).map((r) => [r.at, r.pitch]), [[0, 60], [500, 64], [1000, 60], [1500, 64]]);
  assert.equal(rowsOf(d)[2].slide, 1);
  assert.equal(duplicateRows(m, rowsOf(m), 1500), m);
});

test('adjustRows changes velocity or fine tune of every segment, keeps ranges and does nothing when nothing changes', () => {
  const t = mk('1:3000:1000:60:100;tie=0 2:0:1000:60:100;tie=0');
  const up = adjustRows(t, rowsOf(t), (n) => ({ velocity: n.velocity + 40 }));
  assert.deepEqual(up.notes.map((i) => i.note.velocity), [127, 127]);
  assert.equal(adjustRows(up, rowsOf(up), (n) => ({ velocity: n.velocity + 1 })), up);
  const fine = adjustRows(t, rowsOf(t), (n) => ({ fine: (n.fine ?? 0) - 500 }));
  assert.deepEqual(fine.notes.map((i) => i.note.fine), [-100, -100]);
});

test('slice at a snapped point makes two notes and keeps the fields', () => {
  const m = mk('1:0:1000:60:96;pan=9');
  const s = sliceNote(m, rowsOf(m)[0], 500);
  assert.equal(format(s), '1:0:500:60:96;pan=9 1:500:500:60:96;pan=9');
  assert.equal(sliceNote(m, rowsOf(m)[0], 0), m);
  assert.equal(sliceNote(m, rowsOf(m)[0], 1000), m);
});

test('removeRows still works on duplicated rows (sanity)', () => {
  const m = mk('1:0:500:60:96 1:500:500:64:90');
  assert.equal(rowsOf(removeRows(m, [rowsOf(m)[0]])).length, 1);
});
