import test from 'node:test';
import assert from 'node:assert/strict';
import { extendedParts } from '../clip-parts.mjs';

test('a clip with one placement gets longer and keeps its start and name', () => {
  const r = extendedParts([{ at: 3, length: 2, name: 'verse' }], 6);
  assert.deepEqual(r.parts, [{ at: 3, length: 6, name: 'verse' }]);
  assert.equal(r.length, 6);
  assert.equal(r.clamped, false);
});

test('every other placement of the clip stays as it is (issue 243)', () => {
  const parts = [{ at: 1, length: 2, name: 'a' }, { at: 9, length: 4, name: 'b' }, { at: 20, length: 1 }];
  const r = extendedParts(parts, 6);
  assert.deepEqual(r.parts, [{ at: 1, length: 6, name: 'a' }, { at: 9, length: 4, name: 'b' }, { at: 20, length: 1, name: '' }]);
  assert.equal(r.parts.length, 3);
  assert.equal(r.clamped, false);
});

test('a longer clip stops just before the next placement instead of running into it', () => {
  const r = extendedParts([{ at: 1, length: 2 }, { at: 5, length: 2 }], 10);
  assert.equal(r.parts[0].length, 4);   // bars 1 to 4, the next placement starts at bar 5
  assert.deepEqual(r.parts[1], { at: 5, length: 2, name: '' });
  assert.equal(r.clamped, true);
});

test('the nearest later placement limits it, whatever the order of the list', () => {
  const r = extendedParts([{ at: 2, length: 1 }, { at: 30, length: 1 }, { at: 6, length: 1 }], 20);
  assert.equal(r.parts[0].length, 4);   // bars 2 to 5, then bar 6
  assert.equal(r.clamped, true);
});

test('a placement before the open one does not limit it', () => {
  const r = extendedParts([{ at: 8, length: 2 }, { at: 1, length: 2 }], 5);
  assert.equal(r.parts[0].length, 5);
  assert.equal(r.clamped, false);
  assert.deepEqual(r.parts[1], { at: 1, length: 2, name: '' });
});

test('the clip never gets shorter, also when the room is smaller than it already is', () => {
  const r = extendedParts([{ at: 1, length: 8 }, { at: 4, length: 1 }], 12);
  assert.equal(r.parts[0].length, 8);
  assert.equal(r.clamped, true);
});

test('no placements at all gives one placement at bar 1', () => {
  assert.deepEqual(extendedParts(undefined, 4).parts, [{ at: 1, length: 4, name: '' }]);
  assert.deepEqual(extendedParts([], 4).parts, [{ at: 1, length: 4, name: '' }]);
});

test('junk entries and a fractional or missing length are handled', () => {
  const r = extendedParts([null, { at: 1, length: 1 }, { at: 'x', length: 2 }], 2.2);
  assert.deepEqual(r.parts, [{ at: 1, length: 3, name: '' }]);
  assert.equal(extendedParts([{ at: 1, length: 1 }], undefined).length, 1);
});

test('the list that was given is not changed', () => {
  const parts = [{ at: 1, length: 2, name: 'a' }, { at: 9, length: 4 }];
  const copy = JSON.parse(JSON.stringify(parts));
  extendedParts(parts, 6);
  assert.deepEqual(parts, copy);
});
