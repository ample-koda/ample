import test from 'node:test';
import assert from 'node:assert/strict';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-stretch-worker.js');

test('local stretch preserves duration and tone pitch at double speed', () => {
  const rate = 24000, input = new Int16Array(rate);
  for (let i = 0; i < input.length; i++) input[i] = Math.round(Math.sin(2 * Math.PI * 440 * i / rate) * 20000);
  self.onmessage({ data: { channels: [input.buffer], speed: 200, sampleRate: rate } });
  const output = new Float32Array(self.message.channels[0]);
  assert.equal(output.length, input.length);
  let crossings = 0;
  for (let i = 1201; i < 10800; i++) if (output[i - 1] <= 0 && output[i] > 0) crossings++;
  const hz = crossings / ((10800 - 1200) / rate);
  assert.ok(Math.abs(hz - 440) < 20, `expected about 440 Hz, got ${hz}`);
});

test('a speed that is not a number or a curve is refused (the server renders it), and the memory estimate grows with the length', async () => {
  const { processStretchChannels, stretchWorkingSetBytes } = await import('../clientside-stretch.mjs');
  await assert.rejects(processStretchChannels([new Float32Array(8)], 24000, { speed: 'fast' }), /server renderer/);
  assert.ok(stretchWorkingSetBytes(48000, 2) > stretchWorkingSetBytes(24000, 2));
});

test('a speed per sample moves the grains along the output like TimeStretch.Speed, and 100 everywhere changes nothing', async () => {
  const { stretchWithCurve, speedCurve } = await import('../clientside-stretch.mjs');
  const input = Int32Array.from({ length: 4000 }, (_, i) => (i % 400 < 40 ? 8000 : 0));
  assert.equal(await stretchWithCurve(input, new Int32Array(4000).fill(100), 8000), input);
  const doubled = await stretchWithCurve(input, new Int32Array(4000).fill(200), 8000);
  assert.equal(doubled.length, input.length);
  const first = (a, from) => { for (let i = from; i < a.length; i++) if (a[i] > 2000) return i; return -1; };
  assert.ok(first(doubled, 500) < first(input, 500) - 100, 'twice as fast: the second blip comes earlier');
  assert.equal(speedCurve('100', 1000, 8000, 1, 10), null);
  const curve = speedCurve('auto(1:100, 2:400)', 1000, 8000, 1, 2000);
  assert.equal(curve[0], 100);
  assert.equal(curve[1000], 400);
  assert.equal(speedCurve('auto(1:10, 2:900)', 1000, 8000, 1, 2000)[1999], 400);   // held between 25 and 400
});
