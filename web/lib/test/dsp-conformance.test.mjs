import test from 'node:test';
import assert from 'node:assert/strict';
import { loadVectors, offlineDeviations, OFFLINE_LIMITS_DB, serverChannels, quantizedInput, deviationDb } from '../dsp-conformance.mjs';
import { CONFORMANCE_PARAMS } from '../dsp-references.mjs';

const vectors = loadVectors();

test('every block that is checked has a vector made by the server, with the same parameters', () => {
  for (const [type, params] of Object.entries(CONFORMANCE_PARAMS)) {
    assert.ok(vectors.cases[type], `${type} has no vector (run node tools/gen-dsp-vectors.mjs)`);
    assert.deepEqual(vectors.cases[type].params, params, `${type}: the vector was made with other parameters`);
    assert.equal(serverChannels(vectors.cases[type])[0].length, vectors.cases[type].frames);
  }
});

test('the offline JS port stays as close to the server as it is now', async () => {
  const deviations = await offlineDeviations(vectors);
  const worse = Object.entries(deviations).filter(([type, db]) => !(db <= OFFLINE_LIMITS_DB[type])).map(([type, db]) => `${type} ${db.toFixed(1)} dB (limit ${OFFLINE_LIMITS_DB[type]})`);
  assert.deepEqual(worse, []);
  assert.deepEqual(Object.keys(OFFLINE_LIMITS_DB).sort(), Object.keys(CONFORMANCE_PARAMS).sort());
});

test('the distance measure is zero-ish for the same signal and large for silence', () => {
  const input = quantizedInput();
  assert.ok(deviationDb(input.channels, input.channels, input.sampleRate) <= -150);
  assert.ok(deviationDb(input.channels, input.channels.map((c) => new Float32Array(c.length)), input.sampleRate) >= -1);
});

test('the vectors were made with the current engine sources (else: node tools/vectors-sync.mjs)', async () => {
  const { engineHash } = await import('../../../tools/vectors-sync.mjs');
  assert.equal(vectors.engineHash, engineHash());
});
