import test from 'node:test';
import assert from 'node:assert/strict';
import { goTargets } from '../history-go.mjs';

const lines = [
  { name: 'studio', head: 'n1', archived: false },
  { name: 'garage', head: 'n2', archived: false },
  { name: 'try', head: 'n2', archived: false },
  { name: 'old', head: 'n2', archived: true },
  { name: 'dropped', head: 'n3', archived: true },
];

test('the current branch stays when it is at the revision (issue 146)', () => {
  assert.deepEqual(goTargets(lines, 'n2', 'try'), { kind: 'current', name: 'try' });
  assert.deepEqual(goTargets(lines, 'n2', 'garage'), { kind: 'current', name: 'garage' });
});

test('one other branch at the revision is where it goes', () => {
  assert.deepEqual(goTargets(lines, 'n1', 'garage'), { kind: 'one', name: 'studio' });
});

test('several branches at the revision are listed by name, without the archived ones', () => {
  assert.deepEqual(goTargets(lines, 'n2', 'studio'), { kind: 'many', names: ['garage', 'try'] });
});

test('a revision that only an archived branch ends at has no place to go to', () => {
  assert.deepEqual(goTargets(lines, 'n3', 'garage'), { kind: 'none' });
  assert.deepEqual(goTargets(lines, 'n9', 'garage'), { kind: 'none' });
  assert.deepEqual(goTargets(undefined, 'n1', 'garage'), { kind: 'none' });
});

test('an archived branch that is the current one does not count', () => {
  assert.deepEqual(goTargets(lines, 'n2', 'old'), { kind: 'many', names: ['garage', 'try'] });
});
