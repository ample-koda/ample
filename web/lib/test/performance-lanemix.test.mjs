import test from 'node:test';
import assert from 'node:assert/strict';
import { CUTOFF, GAIN_DB, createLaneMix, createLoadGuard, cutoffAt, dbToGain, emptyMix, isNeutral, normalizeMix, positionOfCutoff } from '../performance-lanemix.mjs';

test('a mix is read from anything, clamped, and unknown values are the neutral ones', () => {
  assert.deepEqual(normalizeMix(null), emptyMix());
  const m = normalizeMix({ gainDb: 500, pan: -500, cutoff: 1, mute: 'yes', solo: true });
  assert.equal(m.gainDb, GAIN_DB.max);
  assert.equal(m.pan, -100);
  assert.equal(m.cutoff, CUTOFF.min);
  assert.equal(m.mute, false);   // only true is true
  assert.equal(m.solo, true);
  assert.equal(isNeutral(emptyMix()), true);
  assert.equal(isNeutral({ gainDb: -3 }), false);
});

test('gain and the filter scale: silence at the bottom, unity at 0 dB, a log slider for the cutoff', () => {
  assert.equal(dbToGain(GAIN_DB.min), 0);
  assert.ok(Math.abs(dbToGain(0) - 1) < 1e-12);
  assert.ok(Math.abs(dbToGain(-6) - 0.501) < 0.001);
  assert.equal(cutoffAt(0), CUTOFF.min);
  assert.ok(Math.abs(cutoffAt(1) - CUTOFF.max) < 1e-6);
  assert.ok(Math.abs(positionOfCutoff(cutoffAt(0.37)) - 0.37) < 1e-9);
});

test('a change is heard through settings, is session only, and Reset brings back what the layout keeps', () => {
  const mix = createLaneMix(new Map([['Drums', { gainDb: -3 }]]));
  assert.equal(mix.size, 0);
  mix.set('Drums', { gainDb: -12, pan: 50 });
  mix.set('Bass', { cutoff: 800 });
  assert.equal(mix.size, 2);
  const heard = mix.settings(['Drums', 'Bass']);
  assert.ok(Math.abs(heard.get('Drums').gain - dbToGain(-12)) < 1e-12);
  assert.equal(heard.get('Drums').pan, 0.5);
  assert.equal(heard.get('Bass').cutoff, 800);
  mix.reset();
  assert.equal(mix.size, 0);
  assert.ok(Math.abs(mix.settings(['Drums']).get('Drums').gain - dbToGain(-3)) < 1e-12);   // the saved mix, not the neutral one
});

test('mute and solo: a solo silences the other lanes, a mute only its own', () => {
  const mix = createLaneMix();
  const lanes = ['A', 'B', 'C'];
  mix.set('B', { mute: true });
  assert.deepEqual(lanes.map((l) => mix.settings(lanes).get(l).gain > 0), [true, false, true]);
  mix.set('A', { solo: true });
  assert.deepEqual(lanes.map((l) => mix.settings(lanes).get(l).gain > 0), [true, false, false]);
  mix.set('B', { solo: true, mute: false });
  assert.deepEqual(lanes.map((l) => mix.settings(lanes).get(l).gain > 0), [true, true, false]);
});

test('Keep makes what is heard the saved mix and clears the session; the filters can be bypassed without losing their values', () => {
  const mix = createLaneMix();
  mix.set('A', { cutoff: 500, gainDb: -2 });
  assert.deepEqual(mix.filtered(['A', 'B']), ['A']);
  mix.bypassFilters(true);
  assert.equal(mix.settings(['A']).get('A').cutoff, null);   // no filter now
  assert.equal(mix.get('A').cutoff, 500);   // the value is still there
  mix.bypassFilters(false);
  assert.equal(mix.settings(['A']).get('A').cutoff, 500);
  const kept = mix.keep(['A', 'B']);
  assert.equal(kept.get('A').cutoff, 500);
  assert.equal(mix.size, 0);
  mix.set('A', { cutoff: 9000 });
  mix.reset();
  assert.equal(mix.get('A').cutoff, 500);   // Reset goes back to what was kept
});

test('the subscribers hear every change', () => {
  const mix = createLaneMix();
  let n = 0;
  const off = mix.subscribe(() => { n++; });
  mix.set('A', { gainDb: -1 });
  mix.reset();
  off();
  mix.set('A', { gainDb: -2 });
  assert.equal(n, 2);
});

test('the load guard bypasses after a sustained high load and restores after a longer calm, and ignores a spike', () => {
  const guard = createLoadGuard({ threshold: 0.8, release: 0.5, updates: 3 });
  assert.equal(guard.feed(0.95), 'ok');
  assert.equal(guard.feed(0.3), 'ok');   // a spike is not a sustained load
  assert.deepEqual([0.9, 0.9, 0.9].map((p) => guard.feed(p)), ['ok', 'ok', 'bypass']);
  assert.equal(guard.bypassed, true);
  assert.deepEqual([0.2, 0.2, 0.2, 0.2, 0.2].map((p) => guard.feed(p)), ['ok', 'ok', 'ok', 'ok', 'ok']);
  assert.equal(guard.feed(0.2), 'restore');
  assert.equal(guard.bypassed, false);
});
