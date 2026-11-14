import test from 'node:test';
import assert from 'node:assert/strict';
import { createDeckRig } from '../../app/performance-decks.js';
import { makeFactory, reaches } from './support/audio-stub.mjs';

const buf = (sec) => ({ duration: sec });
const mk = (opts, f = makeFactory(() => ({}))) => ({ rig: createDeckRig({ bpm: 120, ctxFactory: f, ...opts }), f, room: f.made[0] });
const find = (room) => ({ dest: room.nodes.find(n => n.kind === 'streamdest'), gains: room.nodes.filter(n => n.kind === 'gain') });

test('cue tap: no connection path from the cue send to the room destination, and it is pre-fader', () => {
  const { room } = mk();
  const { dest, gains } = find(room);
  const [out, busA, xfA, sendA, busB, xfB, sendB] = gains;
  for (const s of [sendA, sendB]) { assert.ok(reaches(s, dest)); assert.equal(reaches(s, room.destination), false); assert.equal(reaches(s, out), false); }
  assert.equal(reaches(dest, room.destination), false);
  // pre-fader: the tap hangs off the bus, not behind the crossfader gain
  assert.ok(busA.out.has(sendA) && busA.out.has(xfA) && !xfA.out.has(sendA));
  assert.ok(busB.out.has(sendB));
  assert.ok(reaches(xfA, room.destination));
  assert.equal(sendA.gain.value, 0);
});

test('without a MediaStreamDestination there is no tap and cue is unavailable', async () => {
  const f = makeFactory(() => ({ noStream: true }));
  const { rig } = mk({}, f);
  assert.equal((await rig.setCueDevice('hp', 'room')).mode, 'unavailable');
  assert.equal(f.made.length, 1);
});

test('cue stays silent without setSinkId and says why', async () => {
  const f = makeFactory(() => ({ noSinkId: true }));
  const { rig, room } = mk({}, f);
  rig.setCue('A', true);
  const r = await rig.setCueDevice('headphones', 'room');
  assert.equal(r.mode, 'unavailable');
  assert.equal(f.made.length, 1);
  assert.ok(find(room).gains[3].gain.target === 0);
  assert.match(rig.state().routing.message, /not available/);
});

test('same device keeps cue silent; default counts as the room device', async () => {
  const { rig, f } = mk();
  rig.setCue('B', true);
  assert.equal((await rig.setCueDevice('x', 'x')).mode, 'same-device');
  assert.equal((await rig.setCueDevice('default', null)).mode, 'same-device');
  assert.equal(f.made.length, 1);
  assert.equal(find(f.made[0]).gains[6].gain.target, 0);
});

test('setSinkId rejection: no cue context left open, cue silent, message names the failure', async () => {
  const f = makeFactory((i) => (i === 1 ? { sinkRejects: 'NotAllowedError' } : {}));
  const { rig, room } = mk({}, f);
  rig.setCue('A', true);
  const r = await rig.setCueDevice('hp', 'room');
  assert.equal(r.mode, 'failed');
  assert.match(r.message, /NotAllowedError/);
  assert.equal(f.made[1].closed, 1);
  assert.equal(find(room).gains[3].gain.target, 0);
});

test('working split: cue listens to the stream on the cue context only; cue gain follows the switch', async () => {
  const { rig, f, room } = mk();
  const r = await rig.setCueDevice('hp', 'room');
  assert.equal(r.mode, 'split');
  const cue = f.made[1];
  assert.equal(cue.sinkId, 'hp');
  const src = cue.nodes.find(n => n.kind === 'streamsource');
  assert.ok(src.out.has(cue.destination));
  assert.equal(src.stream, find(room).dest.stream);
  rig.setCue('A', true);
  const sendA = find(room).gains[3];
  assert.equal(sendA.gain.target, 1);
  rig.setCue('A', false);
  assert.equal(sendA.gain.target, 0);
  // switching device closes the previous cue context and silences first
  rig.setCue('A', true);
  await rig.setCueDevice('hp2');
  assert.equal(cue.closed, 1);
  assert.equal(f.made[2].sinkId, 'hp2');
  assert.equal(rig.state().routing.mode, 'split'); // room device 'room' was kept
  await rig.setCueDevice('room');
  assert.equal(rig.state().routing.mode, 'same-device');
  assert.equal(sendA.gain.target, 0);
});

test('overlapping setCueDevice calls leave at most one cue context open', async () => {
  const { rig, f } = mk();
  await Promise.all([rig.setCueDevice('h1', 'r'), rig.setCueDevice('h2', 'r')]);
  const open = f.made.slice(1).filter(c => c.state !== 'closed');
  assert.equal(open.length, 1);
  assert.equal(open[0].sinkId, 'h2');
});

test('dispose disconnects nodes, stops stream tracks, closes both contexts, and is idempotent', async () => {
  const { rig, f, room } = mk();
  await rig.setCueDevice('hp', 'room');
  rig.load('A', { buffer: buf(2) }); rig.start('A', { onBar: false });
  const { dest, gains } = find(room);
  const src = room.nodes.find(n => n.kind === 'source');
  rig.dispose(); rig.dispose();
  await new Promise(r => setImmediate(r));
  assert.ok(dest.stream.tracks.every(t => t.stopped));
  for (const g of gains) assert.equal(g.out.size, 0);
  assert.ok(src.calls.some(c => c[0] === 'stop'));
  assert.equal(room.closed, 1);
  assert.equal(f.made[1].closed, 1);
  assert.equal(rig.start('A'), false);
  await rig.setCueDevice('x', 'y'); // no new context after dispose
  assert.equal(f.made.length, 2);
});

test('start on the bar with fractional tempo, stop quantised, playing reflects the pending stop', () => {
  const f = makeFactory();
  const rig = createDeckRig({ bpm: 127.3, ctxFactory: f });
  const room = f.made[0], bar = 4 * 60 / 127.3;
  rig.load('A', { buffer: buf(bar), loopBeats: 4, downbeatBeats: 1 });
  room.currentTime = 0.1;
  rig.start('A');
  const s = room.nodes.find(n => n.kind === 'source');
  assert.ok(Math.abs(s.calls[0][1] - bar) < 1e-9);
  assert.ok(Math.abs(s.calls[0][2] - 60 / 127.3) < 1e-9); // starts at the downbeat inside the buffer
  room.currentTime = bar + 0.5;
  rig.stop('A');
  assert.ok(Math.abs(s.calls.at(-1)[1] - 2 * bar) < 1e-9);
  assert.equal(rig.state().playing.A, true);
  room.currentTime = 2 * bar + 0.01; s.onended();
  assert.equal(rig.state().playing.A, false);
});

test('two decks started at different bars stay in phase; drift is 0', () => {
  const f = makeFactory(); const rig = createDeckRig({ bpm: 120, ctxFactory: f }); const room = f.made[0];
  rig.load('A', { buffer: buf(2) }); rig.load('B', { buffer: buf(2) });
  room.currentTime = 0.3; rig.start('A');
  room.currentTime = 5.1; rig.start('B');
  room.currentTime = 9.7;
  const st = rig.state();
  assert.equal(st.drift, 0);
  assert.ok(Math.abs(st.phase.A - st.phase.B) < 1e-9);
  rig.start('A', {}); // already playing: ignored
  assert.equal(room.nodes.filter(n => n.kind === 'source').length, 2);
});

test('material longer than the declared loop is looped at the declared length', () => {
  const f = makeFactory(); const rig = createDeckRig({ bpm: 120, ctxFactory: f });
  rig.load('A', { buffer: buf(2.5), loopBeats: 4 }); rig.start('A', { onBar: false });
  const s = f.made[0].nodes.find(n => n.kind === 'source');
  assert.equal(s.loop, true); assert.equal(s.loopEnd, 2);
});

test('loading a deck while it plays swaps the material at the next bar', () => {
  const f = makeFactory(); const rig = createDeckRig({ bpm: 120, ctxFactory: f }); const room = f.made[0];
  rig.load('A', { buffer: buf(2) }); room.currentTime = 0.5; rig.start('A');
  room.currentTime = 2.5;
  rig.load('A', { buffer: buf(4), loopBeats: 8 });
  const [oldS, newS] = room.nodes.filter(n => n.kind === 'source');
  assert.deepEqual(oldS.calls.at(-1), ['stop', 4]);
  assert.equal(newS.calls[0][1], 4);
  assert.equal(newS.buffer.duration, 4);
  assert.equal(rig.state().phase.A, null); // not started yet: no stale phase against the new loop length
  oldS.onended(); // old source ending must not clear the new one
  assert.equal(rig.state().playing.A, true);
});

test('suspended context: start asks it to resume; state shows the context state', () => {
  const f = makeFactory(); const rig = createDeckRig({ bpm: 120, ctxFactory: f }); const room = f.made[0];
  room.state = 'suspended'; rig.load('A', { buffer: buf(2) }); rig.start('A');
  assert.equal(room.resumed, 1);
  assert.equal(rig.state().context, 'running');
});

test('crossfader: equal power on the node gains, NaN ignored', () => {
  const { rig, room } = mk();
  const [, , xfA, , , xfB] = find(room).gains;
  assert.equal(xfA.gain.value, 1); assert.equal(xfB.gain.value, 0);
  rig.setCrossfade(0.5);
  assert.ok(Math.abs(xfA.gain.target ** 2 + xfB.gain.target ** 2 - 1) < 1e-12);
  rig.setCrossfade(NaN);
  assert.equal(rig.state().crossfade, 0.5);
  rig.setCrossfade(7);
  assert.equal(rig.state().crossfade, 1);
});

test('bad input is rejected early', () => {
  assert.throws(() => createDeckRig({ bpm: 0, ctxFactory: makeFactory() }), RangeError);
  assert.throws(() => createDeckRig({ bpm: NaN, ctxFactory: makeFactory() }), RangeError);
  const { rig } = mk();
  assert.throws(() => rig.load('A', { buffer: null }), TypeError);
  assert.throws(() => rig.load('C', { buffer: buf(1) }), RangeError);
  assert.throws(() => rig.load('A', { buffer: buf(1), loopBeats: 0 }), RangeError);
});
