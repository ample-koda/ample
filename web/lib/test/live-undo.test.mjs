import test from 'node:test';
import assert from 'node:assert/strict';
import { foldAssistantSteps } from '../live-undo.mjs';

test('the steps of the assistant are folded into one when the person did not edit meanwhile', () => {
  // 3 entries before the run, then 4 steps of the assistant
  const undo = ['u1', 'u2', 'u3', 'a1', 'a2', 'a3', 'a4'];
  assert.deepEqual(foldAssistantSteps(undo, 3, null), ['u1', 'u2', 'u3', 'a1']);
});

test('the manual edits made meanwhile stay, each one undoable on its own (issue 229)', () => {
  const undo = ['u1', 'a1', 'a2', 'm1', 'a3', 'm2'];
  // the assistant started at index 1, the person made the first edit at index 3
  assert.deepEqual(foldAssistantSteps(undo, 1, 3), ['u1', 'a1', 'm1', 'a3', 'm2']);
});

test('earlier history before the run is never touched', () => {
  const undo = ['x1', 'x2', 'a1', 'a2', 'm1'];
  const folded = foldAssistantSteps(undo, 2, 4);
  assert.deepEqual(folded.slice(0, 3), ['x1', 'x2', 'a1']);
  assert.deepEqual(folded, ['x1', 'x2', 'a1', 'm1']);
});

test('a run that made a single step changes nothing', () => {
  assert.deepEqual(foldAssistantSteps(['u1', 'a1'], 1, null), ['u1', 'a1']);
});

test('a run that made no step (start is null) returns the list as it was', () => {
  assert.deepEqual(foldAssistantSteps(['u1', 'm1'], null, 1), ['u1', 'm1']);
  assert.deepEqual(foldAssistantSteps(['u1'], undefined), ['u1']);
});

test('when the person edited right after the first step nothing needs folding', () => {
  assert.deepEqual(foldAssistantSteps(['a1', 'm1', 'm2'], 0, 1), ['a1', 'm1', 'm2']);
});

test('a first manual index before the start, or past the end, is made to fit', () => {
  assert.deepEqual(foldAssistantSteps(['a1', 'a2', 'a3'], 0, -5), ['a1', 'a2', 'a3']);
  assert.deepEqual(foldAssistantSteps(['a1', 'a2', 'a3'], 0, 99), ['a1']);
});

test('the list that was given is not changed, and junk gives an empty list', () => {
  const undo = ['u1', 'a1', 'a2'];
  foldAssistantSteps(undo, 1, null);
  assert.deepEqual(undo, ['u1', 'a1', 'a2']);
  assert.deepEqual(foldAssistantSteps(undefined, 0), []);
  assert.deepEqual(foldAssistantSteps('abc', 0), []);
});
