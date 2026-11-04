import test from 'node:test';
import assert from 'node:assert/strict';
import { barBeats, setBarBeats, setBarBeatsFromText, stepsPerBar } from '../meter.mjs';
import { secondsPerBar } from '../block-activity.mjs';
import { parseEntry } from '../notes-model.mjs';
import { metronomeClicks, gridLevels, SNAPS, autoSnapBeats } from '../lite-roll-model.mjs';
import { clickBars } from '../clientside-click.mjs';

test.afterEach(() => setBarBeats(4));

test('the meter of a song text gives the beats of a bar, the steps of a bar and the seconds of a bar', () => {
  assert.equal(setBarBeatsFromText('composition "x" { ample 1  tempo 100  master-seed 1 }'), 4);
  assert.equal(setBarBeatsFromText('composition "x" { ample 1  tempo 100  meter 4/4  master-seed 1 }'), 4);
  assert.equal(setBarBeatsFromText('composition "w" { ample 1  tempo 96  meter 3/4  master-seed 7 }'), 3);
  assert.equal(stepsPerBar(), 12);
  assert.equal(secondsPerBar(120), 1.5);
  assert.equal(setBarBeatsFromText('composition "s" { ample 1  tempo 120  meter 6/8  master-seed 1 }'), 3);
  assert.equal(setBarBeatsFromText('composition "s" { ample 1  tempo 120  meter 7/8  master-seed 1 }'), 3.5);
  assert.equal(stepsPerBar(), 14);
  assert.equal(setBarBeatsFromText('composition "s" { ample 1  tempo 120  meter 5/4  master-seed 1 }'), 5);
  assert.equal(secondsPerBar(120), 2.5);
  assert.equal(setBarBeatsFromText('no song line at all'), 4);
  assert.equal(secondsPerBar(120), 2);
});

test('a value that is not a meter means 4/4', () => {
  for (const bad of [0, -3, 'x', NaN, 99, null, undefined]) { setBarBeats(bad); assert.equal(barBeats(), 4, String(bad)); }
});

test('the roll grid follows the meter: the bar level, the bar snap, the automatic snap and the metronome accent', () => {
  setBarBeats(3);
  assert.equal(gridLevels(100).find((l) => l.kind === 'bar').beats, 3);
  assert.equal(SNAPS.find((s) => s.id === 'bar').beats, 3);
  assert.equal(autoSnapBeats(1), 3);
  const clicks = metronomeClicks(0, 6 * 960, 960);
  assert.deepEqual(clicks.filter((c) => c.accent).map((c) => c.offset / 960), [0, 3]);
  setBarBeats(4);
  assert.equal(SNAPS.find((s) => s.id === 'bar').beats, 4);
});

test('a note must start inside the bar of the meter (ticks of a note entry)', () => {
  const ppq = 960;
  assert.notEqual(parseEntry('1:2880:480:60:100', ppq), null);   // beat 3 of a 4/4 bar
  setBarBeats(3);
  assert.equal(parseEntry('1:2880:480:60:100', ppq), null);       // not in a 3/4 bar
  assert.notEqual(parseEntry('1:1920:480:60:100', ppq), null);    // beat 2 is
  setBarBeats(4);
});

test('the browser click block has the clicks of the meter: three per bar in 3/4, the first one accented', () => {
  setBarBeats(3);
  const spb = 999, s = clickBars({ accent: 'on', per_beat: '1' }, 2, spb, 44100);
  assert.equal(s.length, 2 * spb);
  const peak = (i) => Math.abs(s[i]) * 32768;
  assert.ok(Math.abs(peak(0) - 16000) < 2, 'accent on the first beat of bar 1');
  assert.ok(Math.abs(peak(333) - 9000) < 2 && Math.abs(peak(666) - 9000) < 2, 'beats 2 and 3 are normal');
  assert.ok(Math.abs(peak(spb) - 16000) < 2, 'accent on the first beat of bar 2');
  assert.ok(Math.abs(peak(spb + 333) - 9000) < 2, 'beat 2 of bar 2');
});
