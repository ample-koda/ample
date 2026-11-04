import test from 'node:test';
import assert from 'node:assert/strict';
import { queryFor, suggestions } from '../template-suggest.mjs';

test('a song without drums is offered a groove, chords without a bass a groove-set', () => {
  assert.deepEqual(suggestions([{ id: 'chords', tpl: 'midi-clip', params: { chords: 'Am F' } }]).map((s) => s.id), ['drums', 'bass']);
  assert.deepEqual(suggestions([{ id: 'drums', tpl: 'drums', params: {} }, { id: 'chords', tpl: 'midi-clip', params: { chords: 'Am F' } }]).map((s) => s.id), ['bass']);
  assert.deepEqual(suggestions([{ id: 'drums', tpl: 'drums', params: {} }, { id: 'bass', tpl: 'bass', params: {} }, { id: 'chords', tpl: 'midi-clip', params: { chords: 'Am' } }]), []);
});

test('drums alone in a small song are offered harmony, and an empty song only a groove', () => {
  assert.deepEqual(suggestions([{ id: 'drums', tpl: 'drums', params: {} }]).map((s) => s.id), ['harmony']);
  assert.deepEqual(suggestions([]).map((s) => s.id), ['drums']);
});

test('the query of a suggestion carries the tempo of the song', () => {
  const [first] = suggestions([]);
  assert.deepEqual(queryFor(first, 96), { kind: 'drum-groove', bpm: 96 });
  assert.deepEqual(queryFor(first, 0), { kind: 'drum-groove' });
});
