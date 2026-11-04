import { test } from 'node:test';
import assert from 'node:assert/strict';
import { laneLineChanges } from '../lane-line.mjs';
import { absolute, format, parse, updateMany } from '../notes-model.mjs';

test('a property line changes only notes between its endpoints and keeps one edit model', () => {
  const model = parse('1:0:1:60:96 1:1:1:64:96 1:2:1:67:96 1:3:1:72:96');
  const notes = absolute(model);
  const changes = laneLineChanges(notes, 0, 2_000_000, -100, 100, 'pan', -100, 100);
  assert.deepEqual([...changes.values()], [{ pan: -100 }, { pan: 0 }, { pan: 100 }]);
  assert.equal(format(updateMany(model, changes)), '1:0:1:60:96;pan=-100 1:1:1:64:96 1:2:1:67:96;pan=100 1:3:1:72:96');
  assert.equal(format(model), '1:0:1:60:96 1:1:1:64:96 1:2:1:67:96 1:3:1:72:96');
});

test('a reverse property line interpolates and clamps its values', () => {
  const notes = [{ at: 0, index: 0 }, { at: 1, index: 1 }, { at: 2, index: 2 }];
  assert.deepEqual([...laneLineChanges(notes, 2, 0, 200, -20, 'velocity', 1, 127).values()], [{ velocity: 1 }, { velocity: 90 }, { velocity: 127 }]);
});
