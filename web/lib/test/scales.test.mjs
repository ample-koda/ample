import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseScale, scaleSet, isRoot, nearestScaleTone, shiftScaleDegree, detectKey, SCALE_DEGREES, ROOTS } from '../scales.mjs';

test('every scale\'s degrees are unique, ascending, start at 0 and fit in an octave', () => {
  for (const [id, degrees] of Object.entries(SCALE_DEGREES)) {
    assert.equal(degrees[0], 0, `${id} should start on the root`);
    assert.equal(new Set(degrees).size, degrees.length, `${id} should have no duplicate degrees`);
    for (let i = 1; i < degrees.length; i++) assert.ok(degrees[i] > degrees[i - 1], `${id} should be ascending`);
    for (const d of degrees) assert.ok(d >= 0 && d <= 11, `${id} degree ${d} out of range`);
  }
});

test('parseScale reads "root:id", "none" and rejects garbage', () => {
  assert.deepEqual(parseScale('c:minor'), { root: 0, scaleId: 'minor' });
  assert.deepEqual(parseScale('"f#:dorian"'), { root: 6, scaleId: 'dorian' });
  assert.deepEqual(parseScale('Bb:major'), { root: 10, scaleId: 'major' });
  assert.equal(parseScale('none'), null);
  assert.equal(parseScale(''), null);
  assert.equal(parseScale(undefined), null);
  assert.equal(parseScale('z:minor'), null);
  assert.equal(parseScale('c:not-a-scale'), null);
});

test('scaleSet and isRoot agree on c major', () => {
  const scale = parseScale('c:major');
  const set = scaleSet(scale);
  for (const pc of [0, 2, 4, 5, 7, 9, 11]) assert.ok(set.has(pc));
  for (const pc of [1, 3, 6, 8, 10]) assert.ok(!set.has(pc));
  assert.ok(isRoot(60, scale));    // C4
  assert.ok(!isRoot(61, scale));   // C#4
  assert.equal(scaleSet(null), null);
});

test('nearestScaleTone leaves in-scale pitches alone and moves out-of-scale pitches to a neighbour in the scale', () => {
  const scale = parseScale('c:major');
  assert.equal(nearestScaleTone(60, scale), 60);           // C, already in scale
  const moved = nearestScaleTone(61, scale);                // C#, not in c major
  assert.ok([60, 62].includes(moved));
  assert.equal(nearestScaleTone(61, null), 61);              // no scale: unchanged
});

test('shiftScaleDegree moves one degree in the chosen direction and stops at MIDI bounds', () => {
  const scale = parseScale('c:major');
  assert.equal(shiftScaleDegree(60, 1, scale), 62);
  assert.equal(shiftScaleDegree(64, -1, scale), 62);
  assert.equal(shiftScaleDegree(61, 1, scale), 62);
  assert.equal(shiftScaleDegree(61, -1, scale), 60);
  assert.equal(shiftScaleDegree(127, 1, scale), 127);
  assert.equal(shiftScaleDegree(60, 1, null), 60);
});

test('detectKey prefers c major for a C-major scale run and returns null for no notes', () => {
  const notes = [0, 2, 4, 5, 7, 9, 11].map((pc, i) => ({ pitch: 60 + pc, length: 480, at: i * 480 }));
  const key = detectKey(notes);
  assert.equal(key.root, 0);
  assert.equal(key.mode, 'major');
  assert.equal(detectKey([]), null);
});

test('ROOTS has 12 names, one per pitch class', () => { assert.equal(ROOTS.length, 12); });
