import test from 'node:test';
import assert from 'node:assert/strict';
import { staleSelections } from '../review-selection.mjs';
import { regenerateOptions } from '../ai-review-text.mjs';

const changes = [{ path: 'a.txt', diff: '+one' }, { path: 'b.txt', diff: '+two' }];

test('a check stands while its diff is the one that was reviewed (issue 227)', () => {
  assert.deepEqual(staleSelections(['a.txt'], new Map([['a.txt', '+one']]), changes), []);
});

test('a file whose diff changed, or that is gone, loses its check', () => {
  const reviewed = new Map([['a.txt', '+one'], ['b.txt', '+old'], ['c.txt', '+x']]);
  assert.deepEqual(staleSelections(['a.txt', 'b.txt', 'c.txt'], reviewed, changes), ['b.txt', 'c.txt']);
});

test('a check without a reviewed diff does not stand, and nothing checked gives nothing', () => {
  assert.deepEqual(staleSelections(['a.txt'], new Map(), changes), ['a.txt']);
  assert.deepEqual(staleSelections([], new Map(), changes), []);
  assert.deepEqual(staleSelections(['a.txt'], new Map([['a.txt', '']]), undefined), ['a.txt']);
});

test('Regenerate gives the target of the old request back, and the old prompt as the hint (issue 210)', () => {
  const task = { song: 'a.amplet', path: 'a.amplet', block: 'bass', scope: 'selection', sessionId: 's1', prompt: 'make it warmer' };
  assert.deepEqual(regenerateOptions({ song: 'other.amplet', x: 1 }, task), { song: 'a.amplet', x: 1, path: 'a.amplet', block: 'bass', scope: 'selection', sessionId: 's1', hint: 'make it warmer' });
});

test('Regenerate keeps the caller options where the task says nothing', () => {
  assert.deepEqual(regenerateOptions({ path: 'p', scope: 'file' }, { prompt: 'x', scope: '' }), { path: 'p', scope: 'file', hint: 'x' });
  assert.deepEqual(regenerateOptions({}, undefined), { hint: undefined });
});
