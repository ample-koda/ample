import assert from 'node:assert/strict';
import test from 'node:test';
import { builtinDrumSongSupported, drumWorkingSetBytes, renderBuiltinDrumChannels } from '../clientside-drums.mjs';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-drums-worker.js');

function render(params, seed = '17', activeBars = [1]) {
  self.onmessage({ data: { params, seed, sampleRate: 8000, firstBar: 1, bars: 1, activeBars } });
  return self.message.error ? self.message : new Float32Array(self.message.channels[0]);
}

test('built-in drum patterns are rendered locally from the exact seeded integer synthesis', () => {
  const common = { kit: 'builtin', pattern: 'beat-A', song: '', patterns: '', tempo: 120 };
  const a = render(common), again = render(common), b = render({ ...common, pattern: 'beat-B' });
  assert.deepEqual(a, again);
  assert.notDeepEqual(a, b);
  assert.ok(Math.max(...a) > 0.5);
});

test('built-in drum song sequencing maps the main pattern and validates unsupported named grids', () => {
  const common = { kit: 'builtin', pattern: 'beat-A', song: 'beat-B pattern', patterns: '', tempo: 120 };
  assert.equal(builtinDrumSongSupported(common), true);
  assert.equal(builtinDrumSongSupported({ ...common, song: 'verse' }), false);
  assert.equal(builtinDrumSongSupported({ ...common, patterns: 'verse=kick:x...' }), false);
  // a grid in the pattern, or another number of steps in a bar, is rendered by the server (the built-in sound reads grids and steps there)
  assert.equal(builtinDrumSongSupported({ ...common, pattern: 'kick:x...x...' }), false);
  assert.equal(builtinDrumSongSupported({ ...common, steps: '8' }), false);
  assert.equal(builtinDrumSongSupported({ ...common, steps: '16' }), true);
  const output = render(common, '17', [1]);
  assert.ok(Math.max(...output.slice(3000, 4500)) > 0.5);
});

test('built-in drum buffers retain bounded working-set accounting', () => {
  assert.equal(drumWorkingSetBytes(100), 600);
});

test('built-in drum preview cancellation terminates its worker', async () => {
  const original = globalThis.Worker;
  class PendingWorker {
    static instance;
    constructor() { PendingWorker.instance = this; }
    postMessage() {}
    terminate() { this.terminated = true; }
  }
  globalThis.Worker = PendingWorker;
  try {
    const controller = new AbortController();
    const pending = renderBuiltinDrumChannels({ tempo: 120 }, '17', 44100, 1, 1, [1], controller.signal);
    controller.abort();
    await assert.rejects(pending, { name: 'AbortError' });
    assert.equal(PendingWorker.instance.terminated, true);
  } finally { globalThis.Worker = original; }
});
