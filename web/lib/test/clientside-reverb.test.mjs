import assert from 'node:assert/strict';
import test from 'node:test';
import { processReverbBuffer, processReverbChannels, reverbWorkingSetBytes } from '../clientside-reverb.mjs';

test('reverb keeps the dry sound, adds a tail after it and mix 0 leaves the sound alone', () => {
  const input = new Float32Array(400); input[0] = .5;
  const params = { size: '50%', decay: '60%', mix: '25%' };
  const wet = processReverbBuffer([input], params, 1000)[0];
  assert.ok(Math.abs(wet[0] - (16384 / 32767)) < 1e-7, 'the first sample is the dry sound (the room comes after it)');
  assert.ok(wet.slice(20).some((v) => v !== 0), 'there is a tail');
  const dry = processReverbBuffer([input], { ...params, mix: '0%' }, 1000)[0];
  assert.deepEqual([...dry], [...Float32Array.from(input, (v) => Math.round(v * 32767) / 32767)]);
});

test('stereo reverb uses different left and right comb taps', () => {
  const left = new Float32Array(128), right = new Float32Array(128); left[0] = right[0] = .5;
  const output = processReverbBuffer([left, right], { size: '50', decay: '60', mix: '100' }, 1000);
  assert.equal(output.length, 2);
  assert.notDeepEqual(output[0], output[1]);
});

test('reverb working-set estimate includes input, wet state and output buffers', () => {
  assert.equal(reverbWorkingSetBytes(100, 2), (100 * 2 * 20) + 65536);
  assert.ok(reverbWorkingSetBytes(100, 2) > reverbWorkingSetBytes(100, 1));
});

test('reverb processing observes cancellation between bounded chunks', () => {
  let calls = 0;
  const input = new Float32Array(20000).fill(.25), signal = { throwIfAborted() { if (++calls === 3) { const error = new Error('cancelled'); error.name = 'AbortError'; throw error; } } };
  assert.throws(() => processReverbBuffer([input], { size: 50, decay: 60, mix: 25 }, 44100, signal), { name: 'AbortError' });
});

test('reverb worker is terminated when its preview is cancelled', async () => {
  const original = globalThis.Worker;
  class PendingWorker {
    static instance;
    constructor() { PendingWorker.instance = this; }
    postMessage() {}
    terminate() { this.terminated = true; }
  }
  globalThis.Worker = PendingWorker;
  try {
    const controller = new AbortController(), pending = processReverbChannels([Float32Array.of(.1)], {}, 44100, controller.signal);
    controller.abort();
    await assert.rejects(pending, { name: 'AbortError' });
    assert.equal(PendingWorker.instance.terminated, true);
  } finally { globalThis.Worker = original; }
});
