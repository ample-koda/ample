import assert from 'node:assert/strict';
import test from 'node:test';
import { ambienceWorkingSetBytes, processAmbienceBuffer, processAmbienceChannels } from '../clientside-ambience.mjs';

test('ambience uses the same seed to repeat the same segment choices', () => {
  const input = [Float32Array.from({ length: 3000 }, (_, index) => Math.sin(index / 7) * .5)], params = { segment: '0.1 s', crossfade: '0.02 s', variation: '40%' };
  const first = processAmbienceBuffer(input, params, 123456789, 11025, 4000, 0), second = processAmbienceBuffer(input, params, 123456789, 11025, 4000, 0);
  assert.deepEqual(first, second);
  assert.ok(first[0].some((sample) => sample !== 0));
});

test('ambience preserves stereo, crops the source and positions audio at its block bar', () => {
  const left = Float32Array.from({ length: 1200 }, (_, index) => index < 200 ? 0 : .25), right = left.map((sample) => -sample);
  const output = processAmbienceBuffer([left, right], { start: '0.01 s', end: '0.1 s', segment: '0.1 s', crossfade: '0.02 s', variation: '0%' }, 99, 11025, 500, -250);
  assert.equal(output.length, 2);
  assert.ok(output[0].slice(0, 250).every((sample) => sample === 0));
  assert.ok(output[1].some((sample) => sample < 0));
});

test('ambience obeys cancellation and bounded working-set estimate', () => {
  let calls = 0;
  assert.throws(() => processAmbienceBuffer([new Float32Array(3000).fill(.25)], { segment: '.1', crossfade: '.02', variation: 0 }, 2, 11025, 3000, 0, { throwIfAborted() { if (++calls === 2) throw new DOMException('Cancelled', 'AbortError'); } }), { name: 'AbortError' });
  assert.equal(ambienceWorkingSetBytes(100, 200, 2, 50), 100 * 2 * 4 + 250 * 2 * 8 + 200 * 2 * 4);
});

test('ambience worker is terminated when cancelled', async () => {
  const original = globalThis.Worker;
  class PendingWorker { static instance; constructor() { PendingWorker.instance = this; } postMessage() {} terminate() { this.terminated = true; } }
  globalThis.Worker = PendingWorker;
  try {
    const controller = new AbortController(), pending = processAmbienceChannels([Float32Array.of(.1)], {}, 1, 11025, 100, 0, controller.signal);
    controller.abort();
    await assert.rejects(pending, { name: 'AbortError' });
    assert.equal(PendingWorker.instance.terminated, true);
  } finally { globalThis.Worker = original; }
});
