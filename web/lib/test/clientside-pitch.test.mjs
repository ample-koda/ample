import test from 'node:test';
import assert from 'node:assert/strict';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-pitch-worker.js');

for (const method of ['granular', 'wsola']) {
  test(`local ${method} pitch shift preserves duration and raises a tone by an octave`, () => {
    const rate = 24000, input = new Int16Array(rate);
    for (let i = 0; i < input.length; i++) input[i] = Math.round(Math.sin(2 * Math.PI * 440 * i / rate) * 20000);
    self.onmessage({ data: { channels: [input.buffer], cents: 1200, method, sampleRate: rate } });
    const output = new Float32Array(self.message.channels[0]);
    assert.equal(output.length, input.length);
    let crossings = 0;
    for (let i = 1; i < output.length; i++) if (i > 1200 && i < output.length - 1200 && output[i - 1] <= 0 && output[i] > 0) crossings++;
    const hz = crossings / ((output.length - 2400) / rate);
    assert.ok(Math.abs(hz - 880) < 20, `expected about 880 Hz, got ${hz}`);
    assert.ok(output.every((sample) => Number.isFinite(sample) && Math.abs(sample) <= 1));
  });
}

test('voice method reports its server fallback and worker budget grows with duration', async () => {
  self.onmessage({ data: { channels: [new Int16Array(100).buffer], cents: 100, method: 'voice', sampleRate: 24000 } });
  assert.match(self.message.error, /server-rendered/);
  const { pitchWorkingSetBytes } = await import('../clientside-pitch.mjs');
  assert.ok(pitchWorkingSetBytes(48000, 2, 'wsola') > pitchWorkingSetBytes(24000, 2, 'wsola'));
});
