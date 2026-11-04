import test from 'node:test';
import assert from 'node:assert/strict';
import { sequencePreviewPlan, sequencerWorkingSetBytes } from '../clientside-sequencer.mjs';

const sequence = `sequencer 1
channel bass bass vol=-6
channel lead notes wave=triangle vol=-10
pattern verse bars=2
  bass 1:0:1:45:100 1:2:1:45:90 2:0:1:43:100 2:2:1:43:90
  lead 1:0:1:69:96 1:1:1:72:96 1:2:2:76:96 2:0:2:74:96
pattern chorus bars=2
  bass 1:0:2:41:100 2:0:2:48:100
  lead 1:0:1:77:100 1:1:1:76:96 1:2:2:72:96 2:0:2:69:96
playlist
  clip verse track=1 at=1 for=4
  clip chorus track=1 at=5 for=4
`;

test('plans the embedded built-in channels across playlist clips and named lanes', () => {
  const plan = sequencePreviewPlan(sequence, 'lane1=bass lane2=lead', 1, 8);
  assert.ok(plan);
  assert.deepEqual(plan.lanes, [1, 2]);
  assert.deepEqual(plan.channels.map(({ channel, notes, lanes }) => [channel.name, lanes, notes.length, notes[0]]), [
    ['bass', [1], 12, { bar: 1, beat: 0, beats: 1, pitch: 33, velocity: 100, pan: 0 }],
    ['lead', [2], 16, { bar: 1, beat: 0, beats: 1, pitch: 69, velocity: 96, pan: 0 }],
  ]);
});

test('respects mute and solo and treats unsupported channel types as a whole-block fallback', () => {
  assert.equal(sequencePreviewPlan(sequence.replace('channel bass bass vol=-6', 'channel bass bass mute=on vol=-6'), 'lane1=bass lane2=lead', 1, 8)?.channels[0].channel.name, 'lead');
  assert.equal(sequencePreviewPlan(sequence.replace('channel bass bass vol=-6', 'channel bass gm file=font.sf2'), 'lane1=bass lane2=lead', 1, 8), null);
});

test('keeps audio output budgeting proportional to all lane buffers', () => {
  assert.equal(sequencerWorkingSetBytes(44100, 0), 44100 * 16);
  assert.equal(sequencerWorkingSetBytes(44100, 8), 44100 * 80);
});
