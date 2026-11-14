import test from 'node:test';
import assert from 'node:assert/strict';
import { startLive } from '../../app/live-play.js';

test('the scheduler releases a crossing note at the loop seam and loops to zero after a mid-clip start', () => {
  const ramps = [], events = [];
  class Context {
    constructor() { this.currentTime = 0; this.sampleRate = 1000; this.destination = {}; }
    resume() {}
    createBuffer() { return { getChannelData: () => new Float32Array(1000) }; }
    createChannelSplitter() { return { connect(node) { return node; }, disconnect() {} }; }
    createChannelMerger() { return { connect(node) { return node; }, disconnect() {} }; }
    createAnalyser() { return { fftSize: 0, connect(node) { return node; }, disconnect() {}, getFloatTimeDomainData() {} }; }
    createWaveShaper() { return { curve: null, connect(node) { return node; }, disconnect() {} }; }   // failed on main before this change: the output path now creates a wave shaper
    createDynamicsCompressor() { return { connect(node) { return node; }, disconnect() {} }; }
    createGain() { return { context: this, connect(node) { return node; }, disconnect() {}, gain: { value: 1, setValueAtTime() {}, setTargetAtTime() {}, linearRampToValueAtTime: (value, at) => ramps.push({ value, at }) } }; }
  }
  const previous = globalThis.window;
  globalThis.window = { AudioContext: Context };
  let stopped = 0;
  try {
    const player = startLive({ rows: () => [{ at: 3000, length: 2000, pitch: 60, velocity: 96 }], bpm: () => 120, ppq: 1000, instrument: () => ({ program: 0, channel: 0 }), from: 1.5, loopStart: 0, loop: 2, end: 2, renderEvent: (context, _output, event) => { events.push(event); context.currentTime = 0.7; return true; }, onStop: () => stopped++ });
    assert.equal(events[0].duration, 0.5);
    assert.ok(ramps.some((ramp) => ramp.value === 0 && Math.abs(ramp.at - 0.56) < 0.000001));
    assert.ok(player.time() < 1.5);
    player.stop(); player.stop();
    assert.equal(stopped, 1);
  } finally { globalThis.window = previous; }
});
