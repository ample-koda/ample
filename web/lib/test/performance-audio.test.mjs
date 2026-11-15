import test from 'node:test';
import assert from 'node:assert/strict';
import { createClipPlayer } from '../performance-audio.mjs';
import { createPerformance } from '../../app/performance.js';

function rig() {
  const log = [];
  const timers = { list: [], set(fn, ms) { this.list.push({ fn, ms, live: true }); return this.list.length - 1; }, clear(id) { if (this.list[id]) this.list[id].live = false; } };
  const ctx = {
    currentTime: 10,
    createGain() {
      const calls = [];
      const g = { gain: { value: 0, calls, cancelScheduledValues: (t) => calls.push(['cancel', t]), setValueAtTime: (v, t) => calls.push(['set', v, t]) },
        connect: (n) => { log.push(['gain.connect', n === 'out']); }, disconnect: () => log.push(['gain.disconnect']) };
      log.push(['gain', g]);
      return g;
    },
    createBufferSource() {
      const s = { loop: false, buffer: null, started: [], stopped: 0, connect: () => {}, disconnect: () => log.push(['src.disconnect']),
        start(t) { this.started.push(t); }, stop() { this.stopped++; } };
      log.push(['src', s]);
      return s;
    },
  };
  const buffers = new Map([['kick', { id: 'buf-kick' }], ['snare', { id: 'buf-snare' }]]);
  const player = createClipPlayer({ ctx, output: 'out', bufferOf: (clip) => buffers.get(clip.id), timers });
  const sources = () => log.filter((e) => e[0] === 'src').map((e) => e[1]);
  const gains = () => log.filter((e) => e[0] === 'gain').map((e) => e[1]);
  return { ctx, player, log, timers, sources, gains };
}

test('a clip starts at the given time on a looping source with its buffer', () => {
  const r = rig();
  r.player.play({ id: 'kick' }, 12, 'drums');
  const [source] = r.sources();
  assert.deepEqual(source.started, [12]);
  assert.equal(source.loop, true);
  assert.deepEqual(source.buffer, { id: 'buf-kick' });
  assert.equal(r.player.count, 1);
});

test('a time that has passed starts the clip at once, at the clock', () => {
  const r = rig();
  r.player.play({ id: 'kick' }, 5, 'drums');
  assert.deepEqual(r.sources()[0].started, [10]);
});

test('a clip without a loaded buffer is refused and nothing is scheduled', () => {
  const r = rig();
  assert.throws(() => r.player.play({ id: 'missing', label: 'Missing loop' }, 12, 'drums'), /Missing loop.*not loaded/);
  assert.equal(r.sources().length, 0);
});

test('stop without a time releases the source now', () => {
  const r = rig();
  const handle = r.player.play({ id: 'kick' }, 12, 'drums');
  handle.stop();
  assert.equal(r.sources()[0].stopped, 1);
  assert.equal(r.player.count, 0);
  handle.stop();
  assert.equal(r.sources()[0].stopped, 1, 'a second stop does nothing');
});

test('stop at a time silences the clip there and releases the source shortly after', () => {
  const r = rig();
  const handle = r.player.play({ id: 'kick' }, 12, 'drums');
  handle.stop(14);
  const calls = r.gains()[0].gain.calls;
  assert.deepEqual(calls.at(-1), ['set', 0, 14]);
  assert.equal(r.sources()[0].stopped, 0);
  assert.equal(r.timers.list.at(-1).ms, 4150);
  r.timers.list.at(-1).fn();
  assert.equal(r.sources()[0].stopped, 1);
});

test('taking back a stop keeps the clip: full level again and no release', () => {
  const r = rig();
  const handle = r.player.play({ id: 'kick' }, 12, 'drums');
  handle.stop(14);
  handle.cancelStop();
  assert.equal(r.timers.list.at(-1).live, false);
  assert.deepEqual(r.gains()[0].gain.calls.at(-1), ['set', 1, 10]);
  assert.equal(r.sources()[0].stopped, 0);
  assert.equal(r.player.count, 1);
});

test('cancel before the start releases the source', () => {
  const r = rig();
  r.player.play({ id: 'kick' }, 12, 'drums').cancel();
  assert.equal(r.sources()[0].stopped, 1);
  assert.equal(r.player.count, 0);
});

test('releaseAll ends every source that is left', () => {
  const r = rig();
  r.player.play({ id: 'kick' }, 12, 'drums');
  r.player.play({ id: 'snare' }, 12, 'drums');
  r.player.releaseAll();
  assert.equal(r.player.count, 0);
  assert.deepEqual(r.sources().map((s) => s.stopped), [1, 1]);
});

test('the player needs a context, an output and bufferOf', () => {
  assert.throws(() => createClipPlayer({ ctx: null, output: 'out', bufferOf: () => null }), TypeError);
  assert.throws(() => createClipPlayer({ ctx: {}, output: 'out' }), TypeError);
});

test('with the launch engine: a queued replacement stops the old clip at the same bar and Stop all releases everything', () => {
  const r = rig();
  const engine = createPerformance({ now: () => r.ctx.currentTime, play: r.player.play, bpm: 120, beatsPerBar: 4, startLead: 0 });
  engine.addClip('drums', { id: 'kick' });
  engine.addClip('drums', { id: 'snare' });
  engine.launch('drums', 'kick');
  r.ctx.currentTime = 11;
  assert.equal(engine.state().drums[0].status, 'playing');
  const when = engine.launch('drums', 'snare');
  assert.equal(when, 12);
  assert.deepEqual(r.sources()[1].started, [12]);
  assert.deepEqual(r.gains()[0].gain.calls.at(-1), ['set', 0, 12]);
  engine.cancelQueued('drums');
  assert.deepEqual(r.gains()[0].gain.calls.at(-1), ['set', 1, 11]);
  assert.equal(r.sources()[1].stopped, 1);
  engine.stopAll();
  assert.equal(r.player.count, 0);
});

test('a lane bus puts the filter on the path only while it closes the sound, and sets gain and pan from the session mix (issue 276)', async () => {
  const { createLaneBuses } = await import('../performance-audio.mjs');
  const log = [];
  const param = (name) => ({ value: 0, setTargetAtTime(v) { log.push([name, v]); this.value = v; }, cancelScheduledValues() {} });
  const node = (name, extra = {}) => ({ name, connected: [], connect(n) { this.connected = [n]; }, disconnect() { this.connected = []; }, ...extra });
  const ctx = {
    currentTime: 1,
    createGain: () => node('gain', { gain: param('gain') }),
    createBiquadFilter: () => node('filter', { frequency: param('cutoff'), type: '' }),
    createStereoPanner: () => node('pan', { pan: param('pan') }),
  };
  const out = node('out');
  const buses = createLaneBuses({ ctx, output: out });
  const input = buses.input('Drums');
  buses.apply(new Map([['Drums', { gain: 0.5, pan: -0.25, cutoff: null }]]));
  assert.equal(input.connected[0].name, 'pan', 'no filter on the path while it is open');
  assert.deepEqual(log.filter(([n]) => n !== 'cutoff'), [['gain', 0.5], ['pan', -0.25]]);
  buses.apply(new Map([['Drums', { gain: 1, pan: 0, cutoff: 800 }]]));
  assert.equal(input.connected[0].name, 'filter', 'the filter is on the path while it closes the sound');
  assert.equal(log.at(-1)[0], 'cutoff');
  assert.ok(Math.abs(log.at(-1)[1] - 800 * 1.554) < 0.01, 'the corner of the second-order filter sits where the song filter has its -3 dB point');
  buses.apply(new Map([['Drums', { gain: 1, pan: 0, cutoff: null }]]));
  assert.equal(input.connected[0].name, 'pan', 'bypassed again');
  buses.releaseAll();
  assert.equal(buses.count, 0);
});

test('the clip player connects a clip to the bus of its lane when it is given one', () => {
  const calls = [];
  const ctx = { currentTime: 0, createGain: () => ({ gain: { value: 1, cancelScheduledValues() {}, setValueAtTime() {} }, connect: (n) => calls.push(n), disconnect() {} }), createBufferSource: () => ({ connect() {}, disconnect() {}, start() {}, stop() {} }) };
  const player = createClipPlayer({ ctx, output: 'room', outputFor: (lane) => `bus:${lane}`, bufferOf: () => ({}) });
  player.play({ id: 'kick', label: 'kick' }, 1, 'Drums');
  assert.deepEqual(calls, ['bus:Drums']);
});

test('the default timers are called as a method of an object: a browser refuses setTimeout with that object as this (Illegal invocation)', () => {
  const real = { set: globalThis.setTimeout, clear: globalThis.clearTimeout };
  const strict = (fn) => function (...args) { if (this !== undefined && this !== globalThis) throw new TypeError('Illegal invocation'); return fn(...args); };
  globalThis.setTimeout = strict(real.set); globalThis.clearTimeout = strict(real.clear);
  try {
    const node = () => ({ connect() {}, disconnect() {}, start() {}, stop() {}, gain: { value: 1, cancelScheduledValues() {}, setValueAtTime() {} } });
    const ctx = { currentTime: 0, createBufferSource: () => ({ ...node(), buffer: null, loop: false }), createGain: node };
    const player = createClipPlayer({ ctx, output: node(), bufferOf: () => ({}) });
    const handle = player.play({ id: 'a', label: 'A' }, 0, 'Lane');
    handle.stop(5);   // a stop at a later time sets a timer
    handle.cancelStop();
    handle.release();
  } finally { globalThis.setTimeout = real.set; globalThis.clearTimeout = real.clear; }
});
