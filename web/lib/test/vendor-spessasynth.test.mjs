import assert from 'node:assert/strict';
import test from 'node:test';

test('the vendored SpessaSynth core loads and exposes the SoundFont classes', async () => {
  const core = await import('../../vendor/spessasynth/spessasynth_core.js');
  for (const name of ['BasicSoundBank', 'SpessaSynthProcessor', 'SoundBankLoader']) assert.equal(typeof core[name], 'function', name);
});
