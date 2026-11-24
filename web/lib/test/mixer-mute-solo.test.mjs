import test from 'node:test';
import assert from 'node:assert/strict';
import { audibleLanes, balanceGains, laneSettings, mixerGraph, roundedDb } from '../live-mixer.mjs';
import { mixAudioBuffers } from '../clientside-mix.mjs';

// The same cases as tests/Ample.Engine.Tests/MixerMuteSoloTests.cs (the server): the per-input mutes and solos of a mixer block.
const VECTORS = [
  [{}, [true, true, true]],
  [{ mutes: 'off off off', solos: 'off off off' }, [true, true, true]],
  [{ mutes: 'on off off' }, [false, true, true]],
  [{ mutes: '"off on"' }, [true, false, true]],
  [{ solos: 'off on off' }, [false, true, false]],
  [{ solos: 'on on off' }, [true, true, false]],
  [{ mutes: 'on off off', solos: 'on off off' }, [false, false, false]],   // a mute wins over a solo
  [{ mutes: 'off on off', solos: 'on on off' }, [true, false, false]],
  [{ mutes: 'ON', solos: 'Off' }, [false, true, true]],
];
for (const [params, expected] of VECTORS) test(`audible lanes ${JSON.stringify(params)}`, () => assert.deepEqual(audibleLanes(params, 3), expected));

test('lane settings carry the gain and pan of a muted lane (nothing is lost by a mute)', () => {
  const [a, b] = laneSettings({ gains: '-6 -3', pan: 'l r', mutes: 'on off' }, 2);
  assert.deepEqual([a.gainDb, a.pan, a.audible], [-6, -32, false]);
  assert.deepEqual([b.gainDb, b.pan, b.audible], [-3, 32, true]);
});

test('the live graph silences a muted lane and keeps its gain value for later', () => {
  const calls = [];
  const param = () => ({ cancelScheduledValues() {}, setTargetAtTime(v) { calls.push(v); } });
  const node = () => ({ gain: param(), connect(n) { return n; }, disconnect() {}, channelCount: 2 });
  const context = { currentTime: 0, createGain: node, createChannelSplitter: node, createChannelMerger: node };
  const mixer = mixerGraph(context, 2);
  calls.length = 0;
  mixer.update({ gains: '0 0', pan: 'c c', mutes: 'on off' });
  assert.equal(calls[0], 0);   // lane 1 gain node: silent
  assert.ok(calls.includes(1));
  calls.length = 0;
  mixer.update({ gains: '0 0', pan: 'c c', mutes: 'off off' });
  assert.equal(calls[0], 1);
});

test('the offline browser mix leaves out muted and non-soloed inputs', async () => {
  const mono = (v) => ({ sampleRate: 48000, channels: [Float32Array.from([v])] });
  const inputs = [{ audio: mono(0.25), index: 0 }, { audio: mono(0.5), index: 1 }];
  const at = async (options) => (await mixAudioBuffers(inputs, { gains: '0 0', pan: 'c c', ...options })).channels[0][0];
  const both = await at({});
  assert.equal(await at({ mutes: 'off off', solos: 'off off' }), both);
  const first = await at({ solos: 'on off' }), second = await at({ mutes: 'on off' });
  assert.ok(Math.abs(second - 0.5) < 0.01 && Math.abs(first - 0.25) < 0.01 && both > second);
  assert.equal(await at({ mutes: 'on on' }), 0);
  assert.equal(await at({ mutes: 'on off', solos: 'on off' }), 0);
});

test('a lane gain is rounded to whole decibels as the server reads it (ReadRoundedDb), in the live and the local mixer (bugs 1 and 3)', async () => {
  const cases = [['-4.5', -5], ['-10.44', -10], ['6dB', 6], ['+3', 3], ['-0.5', -1], ['x', null], ['.5', null], ['', null]];
  for (const [text, db] of cases) assert.equal(roundedDb(text), db, text);
  assert.equal(laneSettings({ gains: '-10.44 6dB' }, 2).map((lane) => lane.gainDb).join(' '), '-10 6');
  const mixed = await mixAudioBuffers([{ audio: { sampleRate: 48000, channels: [Float32Array.from([0.5])] }, index: 0 }], { gains: '-10.44', pan: 'c' });
  assert.ok(Math.abs(mixed.channels[0][0] - 0.5 * 10 ** (-10 / 20)) < 0.002, String(mixed.channels[0][0]));
});

test('pan follows the square-root table of the server, also as a number and as left/right, in both mixers (bugs 2 and 3)', async () => {
  const db = (gain) => 20 * Math.log10(gain);
  assert.ok(Math.abs(db(balanceGains(16).left) - -3.01) < 0.01, 'pan 16: the far side is about -3 dB, as in the build');
  assert.ok(Math.abs(db(balanceGains(24).left) - -6.02) < 0.01);
  assert.deepEqual(balanceGains(0), { left: 1, right: 1 });
  assert.equal(balanceGains(-32).right, 0);
  const one = { audio: { sampleRate: 48000, channels: [Float32Array.from([0.5])] }, index: 0 };
  for (const [pan, left, right] of [['-32', 0.5, 0], ['left', 0.5, 0], ['right', 0, 0.5], ['16', 0.5 * balanceGains(16).left, 0.5]]) {
    const mixed = await mixAudioBuffers([one], { gains: '0', pan });
    assert.equal(mixed.channels.length, 2, `pan ${pan} makes the mix stereo, as on the server`);
    assert.ok(Math.abs(mixed.channels[0][0] - left) < 0.002 && Math.abs(mixed.channels[1][0] - right) < 0.002, `pan ${pan}`);
  }
});
