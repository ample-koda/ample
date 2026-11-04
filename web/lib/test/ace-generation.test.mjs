import test from 'node:test';
import assert from 'node:assert/strict';
import { aceDefaults, aceFamily, withEngineModel } from '../ace-generation.mjs';

test('ACE baseline follows the exact model first and keeps Turbo distinct from SFT/Base', () => {
  assert.deepEqual(aceDefaults({ 'acestep.synthModel': 'turbo' }), { guidance: 1, steps: 8, shift: 3, solver: 'euler', fixed: ['guidance'], family: 'turbo' });
  assert.deepEqual(aceDefaults({ 'acestep.synthModel': 'xl-sft' }), { guidance: 7, steps: 50, shift: 1, solver: 'euler', fixed: [], family: 'sft' });
  assert.equal(aceDefaults({ 'acestep.synthModel': 'base' }).guidance, 7);
  assert.equal(aceDefaults({ 'acestep.synthModel': 'turbo', 'acestep.synthModelFile': 'acestep-v15-xl-sft-BF16.gguf' }).guidance, 7);
  assert.equal(aceDefaults({ 'acestep.synthModel': 'sft', 'acestep.synthModelFile': 'acestep-v15-xl-turbo-Q8_0.gguf' }).guidance, 1);
});

// Same table as ModelRulesTests.Family in the compiler.
test('family table (shared with the compiler)', () => {
  const cases = [
    [{}, 'unknown'], [{ 'acestep.synthModel': 'default' }, 'unknown'], [{ 'acestep.synthModel': 'xl-turbo' }, 'turbo'],
    [{ 'acestep.synthModelFile': 'acestep-v15-sft.gguf' }, 'sft'], [{ 'acestep.synthModelFile': 'acestep-v15-base.gguf' }, 'base'],
    [{ 'acestep.synthModelFile': 'acestep-v15-turbo.gguf' }, 'turbo'], [{ 'acestep.synthModelFile': 'acestep-v15-xl-turbo-Q8_0.gguf' }, 'turbo'],
    [{ 'acestep.synthModelFile': 'mystery.gguf', 'acestep.synthModel': 'turbo' }, 'unknown'],
    [{ 'acestep.synthModelFile': 'turbo.v2.gguf' }, 'turbo'], [{ 'acestep.synthModelFile': 'sftx.gguf' }, 'unknown'],
  ];
  for (const [s, want] of cases) assert.equal(aceFamily(s), want, JSON.stringify(s));
  assert.deepEqual(aceDefaults({}).fixed, []);
  assert.equal(aceDefaults({}).guidance, 1);
});

test('the model a block names in its engine wins over the song, a family over a model file of the song', () => {
  assert.equal(aceFamily(withEngineModel({ 'acestep.synthModel': 'sft' }, 'faer:turbo')), 'turbo');
  assert.equal(aceFamily(withEngineModel({ 'acestep.synthModelFile': 'song-sft.gguf' }, '"local:xl-turbo"')), 'turbo');
  assert.equal(aceFamily(withEngineModel({ 'acestep.synthModel': 'turbo' }, 'faer:acestep-v15-xl-sft-Q8_0.gguf')), 'sft');
  assert.equal(aceFamily(withEngineModel({ 'acestep.synthModel': 'turbo' }, 'faer')), 'turbo');
  assert.equal(aceFamily(withEngineModel({ 'acestep.synthModel': 'turbo' }, '')), 'turbo');
  assert.equal(aceDefaults(withEngineModel({}, 'faer:turbo')).guidance, 1);
  assert.deepEqual(aceDefaults(withEngineModel({}, 'faer:turbo')).fixed, ['guidance']);
});
