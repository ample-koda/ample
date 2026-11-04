import test from 'node:test';
import assert from 'node:assert/strict';
import { rollPlaybackRange, rollPosition } from '../roll-transport.mjs';
import { resolveRollInstrument, rollVoiceKey } from '../roll-instrument.mjs';

test('normal playback starts at the independent playhead; selection preview is explicit', () => {
  const rows = [{ at: 3000, length: 1000 }];
  assert.equal(rollPlaybackRange(0, rows, 1000, false, []).from, 0);
  assert.equal(rollPlaybackRange(500, rows, 1000, false, []).from, 500);
  assert.equal(rollPlaybackRange(0, rows, 1000, false, rows).from, 3000);
});

test('a checked loop remains valid past its end and wraps to clip start, not playback start', () => {
  const range = rollPlaybackRange(9000, [{ at: 3000, length: 2000 }], 1000, true, []);
  assert.deepEqual(range, { from: 0, loopStart: 0, loop: 8000, end: 8000 });
  assert.equal(rollPosition(8000, 3000, 0, 8000), 0);
  assert.equal(rollPosition(8100, 3000, 0, 8000), 100);
  assert.equal(rollPosition(2999, 3000, 0, 8000), 3000);
});

test('instrument resolution follows enabled MIDI effects, ignores disabled links, and terminates cycles', () => {
  const effect = { id: 'shift', tpl: 'transpose', params: { semitones: '12' } }, instrument = { id: 'lead', tpl: 'synth', params: { wave: 'square' } };
  const graph = { nodes: [effect, instrument], links: [{ from: 'clip', to: 'lead', type: 'midi', disabled: true }, { from: 'clip', to: 'shift', type: 'midi' }, { from: 'shift', to: 'clip', type: 'midi' }, { from: 'shift', to: 'lead', type: 'midi' }] };
  assert.deepEqual(resolveRollInstrument(graph, 'clip'), { node: instrument, chain: [effect] });
  instrument.flags = ['disabled'];
  assert.equal(resolveRollInstrument(graph, 'clip'), null);
});

test('voice identity changes with effective seed, instrument settings and note expression', () => {
  const route = { node: { id: 's', tpl: 'synth', params: { cutoff: '2000' } }, chain: [] }, note = { pitch: 60, length: 480, velocity: 90 };
  const key = rollVoiceKey(route, { h: '18446744073709551615' }, note, 120, 44100);
  assert.notEqual(key, rollVoiceKey(route, { h: '18446744073709551614' }, note, 120, 44100));
  assert.notEqual(key, rollVoiceKey(route, { h: '18446744073709551615' }, { ...note, pan: 30 }, 120, 44100));
  route.node.params.cutoff = '1000';
  assert.notEqual(key, rollVoiceKey(route, { h: '18446744073709551615' }, note, 120, 44100));
});
