import test from 'node:test';
import assert from 'node:assert/strict';
import { COLOURS, barsFromDrag, colourOf, dropIndex, dropLineX, duplicateSection, pxPerBar, rects, rulerMarks, tipOf } from '../section-timeline-model.mjs';

const song = [{ name: 'bare', bars: 3 }, { name: 'fill', bars: 1 }, { name: 'verse', bars: 7 }];

test('a name always has the same colour, inside the palette', () => {
  assert.equal(colourOf('fill'), colourOf('fill'));
  for (const name of ['bare', 'fill', 'verse', 'build', 'full', 'down', 'end', 'rest']) assert.ok(colourOf(name) >= 0 && colourOf(name) < COLOURS);
  assert.notEqual(new Set(['bare', 'fill', 'verse', 'build', 'full', 'down', 'end', 'rest'].map(colourOf)).size, 1);
});

test('the bar width fits the whole song when it can and never gets too narrow', () => {
  assert.equal(pxPerBar(11, 440), 40);
  assert.equal(pxPerBar(4, 600), 56);      // not wider than the maximum
  assert.equal(pxPerBar(100, 440), 22);    // too long: the minimum, the timeline scrolls
  assert.equal(pxPerBar(0, 440), 56);
});

test('every section is as wide as its bars and they follow each other', () => {
  assert.deepEqual(rects(song, 10), [{ index: 0, x: 0, width: 30 }, { index: 1, x: 30, width: 10 }, { index: 2, x: 40, width: 70 }]);
});

test('dragging the right edge changes the bars in whole bars, never below 1 or above 999', () => {
  assert.equal(barsFromDrag(3, 26, 10), 6);
  assert.equal(barsFromDrag(3, -14, 10), 2);
  assert.equal(barsFromDrag(3, -500, 10), 1);
  assert.equal(barsFromDrag(3, 99999, 10), 999);
});

test('a dragged section lands before or after the middle of the others', () => {
  // the others without the first section: fill (10 px) and verse (70 px), middles at 5 and 45
  assert.equal(dropIndex(song, 0, 2, 10), 0);
  assert.equal(dropIndex(song, 0, 20, 10), 1);
  assert.equal(dropIndex(song, 0, 60, 10), 2);
  assert.equal(dropIndex(song, 2, 0, 10), 0);   // the last one dragged to the front
  assert.equal(dropLineX(song, 0, 0, 10), 0);
  assert.equal(dropLineX(song, 0, 1, 10), 10);
  assert.equal(dropLineX(song, 0, 2, 10), 80);
});

test('a duplicate is a copy right after the section and the list is not changed in place', () => {
  const copy = duplicateSection(song, 1);
  assert.deepEqual(copy.map((e) => e.name), ['bare', 'fill', 'fill', 'verse']);
  assert.equal(song.length, 3);
  assert.equal(duplicateSection(song, 9), song);
});

test('the ruler numbers are spaced so that they never touch', () => {
  assert.deepEqual(rulerMarks(11, 40).map((m) => m.bar), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
  assert.deepEqual(rulerMarks(45, 22).map((m) => m.bar), [1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31, 33, 35, 37, 39, 41, 43, 45].filter((b) => (b - 1) % 2 === 0));
  assert.equal(rulerMarks(45, 22)[1].x, 44);
});

test('the tip says which bars a section covers', () => {
  assert.equal(tipOf({ name: 'verse', start: 5, bars: 7 }), 'verse: bars 5 to 11 (7 bars)');
  assert.equal(tipOf({ name: 'fill', start: 4, bars: 1 }), 'fill: bar 4');
  assert.equal(tipOf({ name: 'pattern', start: 1, bars: 2 }), 'main: bars 1 to 2 (2 bars)');
});
