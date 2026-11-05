import test from 'node:test';
import assert from 'node:assert/strict';
import { addPart, barAt, movePart, movePartsBy, partsProblem, removePart, repeatPart, resizePart, snapBar, splitPart } from '../part-editing.mjs';

const parts = [{ at: 1, length: 8, name: 'verse' }, { at: 9, length: 8, name: 'chorus' }];

test('snap rounds to the grid, the first bar of a step is 1, 5, 9', () => {
  assert.equal(snapBar(3.4, 1), 3);
  assert.equal(snapBar(6, 4), 5);
  assert.equal(snapBar(7.2, 4), 9);
  assert.equal(barAt(0, 24), 1);
  assert.equal(barAt(47, 24), 2);
});

test('the limits of a part are those of the server', () => {
  assert.equal(partsProblem(parts), null);
  assert.match(partsProblem([{ at: 0, length: 4 }]), /bar 1/);
  assert.match(partsProblem([{ at: 1, length: 0 }]), /one bar/);
  assert.match(partsProblem([{ at: 250, length: 8 }]), /256/);
  assert.equal(partsProblem([{ at: 249, length: 8 }]), null);
  assert.match(partsProblem([{ at: 1, length: 8 }, { at: 8, length: 4 }]), /overlap/);
});

test('a move or a resize gives the new parts, or the reason', () => {
  assert.deepEqual(movePart(parts, 1, 17).parts.map((p) => p.at), [1, 17]);
  assert.match(movePart(parts, 1, 5).problem, /overlap/);
  assert.equal(resizePart(parts, 0, 4).parts[0].length, 4);
  assert.ok(resizePart(parts, 0, 12).problem);
  assert.deepEqual(movePartsBy(parts, [0, 1], 2).parts.map((p) => p.at), [3, 11]);
  assert.ok(movePartsBy(parts, [0, 1], -1).problem, 'one part would start before bar 1');
});

test('split makes two pieces that together cover the clip; the edge is refused', () => {
  const r = splitPart(parts, 0, 5).parts;
  assert.deepEqual(r.map((p) => [p.at, p.length]), [[1, 4], [5, 4], [9, 8]]);
  assert.equal(r[0].name, 'verse');
  assert.ok(splitPart(parts, 0, 1).problem);
  assert.ok(splitPart(parts, 0, 9).problem);
});

test('repeat adds a part after the clip; add and remove keep the list sorted', () => {
  assert.deepEqual(repeatPart([{ at: 1, length: 4, name: '' }], 0).parts.map((p) => [p.at, p.length]), [[1, 4], [5, 4]]);
  assert.ok(repeatPart(parts, 0).problem, 'it would overlap the chorus');
  assert.deepEqual(addPart(parts, 17, 4).parts.map((p) => p.at), [1, 9, 17]);
  assert.equal(removePart([{ at: 1, length: 4, name: '' }], 0), null);
  assert.deepEqual(removePart(parts, 0).parts.map((p) => p.name), ['chorus']);
});
