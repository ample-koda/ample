import test from 'node:test';
import assert from 'node:assert/strict';
import { denoiseWorkingSetBytes, processDenoiseChannel, renderDenoisePreview } from '../../app/studio/preview/denoise.js';

const rms = (samples) => Math.sqrt(samples.reduce((sum, sample) => sum + sample * sample, 0) / samples.length);

test('client denoise preview keeps its duration and finite samples', () => {
  const input = Float32Array.from({ length: 24000 }, (_, i) => .2 * Math.sin(2 * Math.PI * 440 * i / 8000) + .04 * Math.sin(2 * Math.PI * 1337 * i / 8000));
  const output = processDenoiseChannel(input, 8000, { noise: 'auto', reduction: '12', sensitivity: '50' });
  assert.equal(output.length, input.length);
  assert.ok(output.every(Number.isFinite));
  assert.notDeepEqual(output, input);
});

test('client denoise preview uses a manually selected noise stretch', () => {
  const rate = 8000;
  const input = Float32Array.from({ length: rate * 3 }, (_, i) => {
    let seed = (i + 1) * 2654435761 >>> 0;
    seed = (seed * 1664525 + 1013904223) >>> 0;
    const noise = ((seed / 0x100000000) * 2 - 1) * .08;
    return noise + (i >= rate ? .25 * Math.sin(2 * Math.PI * 440 * i / rate) : 0);
  });
  const output = processDenoiseChannel(input, rate, { noise: 'start', learn_from: '0', learn: '.5', reduction: '18', sensitivity: '50' });
  assert.ok(rms(output.subarray(rate)) < rms(input.subarray(rate)));
  assert.ok(output.every(Number.isFinite));
});

test('client denoise preview terminates its worker when cancelled', async () => {
  const OriginalWorker = globalThis.Worker;
  let terminated = false;
  globalThis.Worker = class { postMessage() {} terminate() { terminated = true; } };
  try {
    const input = { length: 2048, sampleRate: 8000, numberOfChannels: 1, getChannelData: () => new Float32Array(2048) };
    const controller = new AbortController(), pending = renderDenoisePreview(input, {}, controller.signal);
    controller.abort();
    await assert.rejects(pending, { name: 'AbortError' });
    assert.equal(terminated, true);
  } finally { globalThis.Worker = OriginalWorker; }
});

test('client denoise chain estimates worker memory and exposes a bounded local size', () => {
  const short = [{ length: 8000, byteLength: 32000 }, { length: 8000, byteLength: 32000 }], long = [{ length: 8_000_000, byteLength: 32_000_000 }, { length: 8_000_000, byteLength: 32_000_000 }];
  assert.ok(denoiseWorkingSetBytes(short) < 128 * 1024 * 1024);
  assert.ok(denoiseWorkingSetBytes(long) > 128 * 1024 * 1024);
});
