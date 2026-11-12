import test from 'node:test';
import assert from 'node:assert/strict';
import { activeChannels } from '../seq-activity.mjs';
import { parseSeq } from '../seq-model.mjs';

const TEXT = 'sequencer 1\nchannel kick drum sample=k.wav\nchannel lead notes wave=square\npattern verse bars=2\n  kick x...x...x...x...|....x...........\n  lead 1:0:1:60:96 2:2:1:64:96\nplaylist\n  clip verse track=1 at=1 for=2\n';
const seq = parseSeq(TEXT).seq;

test('a note lights its channel for its length and then goes out', () => {
  assert.ok(activeChannels(seq, 1.05).has('lead'));
  assert.ok(!activeChannels(seq, 1.6).has('lead'));
  assert.ok(activeChannels(seq, 2.6).has('lead'));   // bar 2, beat 2.4: the second note
});

test('a drum lights on a step with a hit and not on a rest', () => {
  assert.ok(activeChannels(seq, 1.0).has('kick'));
  assert.ok(!activeChannels(seq, 1.07).has('kick'));
});

test('nothing sounds outside the playlist and before the song', () => {
  assert.equal(activeChannels(seq, 4.5).size, 0);
  assert.equal(activeChannels(seq, 0.5).size, 0);
  assert.equal(activeChannels(null, 1).size, 0);
});
