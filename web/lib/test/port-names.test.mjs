import test from 'node:test';
import assert from 'node:assert/strict';
import { streamName, portHint } from '../port-names.mjs';

test('ports are named by the stream they carry', () => {
  const effectIns = [['in', 'audio'], ['render_in', 'render']];
  assert.equal(streamName(effectIns[0], effectIns, false), 'audio');
  assert.equal(streamName(effectIns[1], effectIns, false), 'render');
  const mixerIns = [['in1', 'audio'], ['in2', 'audio'], ['in10', 'audio']];
  assert.deepEqual(mixerIns.map((p) => streamName(p, mixerIns, false)), ['audio1', 'audio2', 'audio10']);
  const outs = [['out', 'audio'], ['render', 'render']];
  assert.equal(streamName(outs[0], outs, true), 'audio');
  const volumeOuts = [['mixed', 'audio'], ['left', 'audio'], ['right', 'audio'], ['out', 'audio'], ['audio', 'audio']];
  assert.equal(streamName(volumeOuts[3], volumeOuts, true), 'out');   // audio is taken by the port of that name
  assert.equal(streamName(['notes', 'midi'], [], false), 'notes');
});

test('the hint names the stream and what it connects to', () => {
  const ins = [['in', 'audio']];
  assert.match(portHint(ins[0], ins, false), /^audio \(older name: in\) · Audio/);
  assert.match(portHint(['notes', 'midi'], [], true), /Connects to a midi input/);
});
