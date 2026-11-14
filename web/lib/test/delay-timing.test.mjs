import test from 'node:test';
import assert from 'node:assert/strict';
import { delayTiming, delayTimingText, syncQuarters } from '../delay-timing.mjs';

test('250 ms at 120 BPM is half a quarter note (issue 164)', () => {
  assert.deepEqual(delayTiming(250, 'off', 120), { ms: 250, quarters: 0.5, synced: false });
  assert.equal(delayTimingText(250, 'off', 120), '250 ms · 0.5 quarter notes at 120 BPM');
});

test('Sync decides the time at the tempo and says so', () => {
  assert.deepEqual(delayTiming(250, '1/4', 120), { ms: 500, quarters: 1, synced: true });
  assert.equal(delayTiming(250, '1/8', 120).ms, 250);
  assert.equal(Math.round(delayTiming(250, '1/8t', 120).ms), 167);
  assert.match(delayTimingText(250, '1/4', 120), /1 quarter note at 120 BPM · Sync 1\/4 decides, Time is not used/);
});

test('note values in quarter notes, and a text that is no value', () => {
  assert.equal(syncQuarters('1/1'), 4);
  assert.equal(syncQuarters('1/16'), 0.25);
  assert.equal(syncQuarters('off'), null);
  assert.equal(syncQuarters(undefined), null);
});
