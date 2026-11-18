import test from 'node:test';
import assert from 'node:assert/strict';
import { preparedOf, preparedUsable } from '../listen-prepared.mjs';

const result = { job: { key: 'block:lead.audio:preview' }, text: 'song', take: 't1' };
const prepared = preparedOf({ result, version: 4, inputs: 'h1', dynamic: false });
const now = { key: 'block:lead.audio:preview', text: 'song', version: 4, inputs: 'h1', dynamic: false };

test('a prepared build is used while the target, the text, the version and the inputs are what they were', () => {
  assert.equal(preparedUsable(prepared, now), true);
});

test('it is not used for another target or quality, another text or version, or other inputs', () => {
  assert.equal(preparedUsable(prepared, { ...now, key: 'block:bass.audio:preview' }), false);
  assert.equal(preparedUsable(prepared, { ...now, key: 'block:lead.audio:full' }), false, 'the quality is part of the key');
  assert.equal(preparedUsable(prepared, { ...now, text: 'song changed' }), false);
  assert.equal(preparedUsable(prepared, { ...now, version: 5 }), false);
  assert.equal(preparedUsable(prepared, { ...now, inputs: 'h2' }), false, 'a component or a sound file changed');
});

test('it is not used when the song reads the clock, or when the inputs are not known, or when there is nothing prepared', () => {
  assert.equal(preparedUsable(preparedOf({ result, version: 4, inputs: 'h1', dynamic: true }), now), false);
  assert.equal(preparedUsable(prepared, { ...now, dynamic: true }), false);
  assert.equal(preparedUsable(preparedOf({ result, version: 4, inputs: null }), { ...now, inputs: null }), false);
  assert.equal(preparedUsable(null, now), false);
});
