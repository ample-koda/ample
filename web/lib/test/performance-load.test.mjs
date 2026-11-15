import test from 'node:test';
import assert from 'node:assert/strict';
import { describeCheck, estimateLoad, levelOf, loadOrder, summarizeLoad, withTimeout } from '../performance-load.mjs';

test('the level words follow the peak load', () => {
  assert.equal(levelOf(0.2), 'comfortable');
  assert.equal(levelOf(0.7), 'tight');
  assert.equal(levelOf(0.95), 'at risk');
});

test('the summary takes the highest peak and the mean of the averages, and says when nothing was measured', () => {
  const summary = summarizeLoad([{ average: 0.2, peak: 0.4 }, { average: 0.4, peak: 0.7 }, { average: 0.3, peak: 0.5 }]);
  assert.equal(summary.measured, true);
  assert.equal(summary.peak, 0.7);
  assert.ok(Math.abs(summary.average - 0.3) < 1e-9);
  assert.equal(summary.level, 'tight');
  assert.equal(summarizeLoad([]).measured, false);
  assert.equal(summarizeLoad([{ average: 0.1, peak: Number.NaN }]).measured, false);
});

test('the estimate grows with the clips and never passes the whole thread', () => {
  assert.equal(estimateLoad(0), 0);
  assert.ok(Math.abs(estimateLoad(10) - 0.3) < 1e-9);
  assert.equal(estimateLoad(1000), 1);
});

test('a device that measures gets the measured result, one that cannot gets a labelled estimate', () => {
  const measured = describeCheck({ supported: true, summary: summarizeLoad([{ average: 0.2, peak: 0.3 }]), estimate: 0.12, clips: 4 });
  assert.match(measured[0], /Measured on this device with 4 clips/);
  assert.match(measured[2], /nothing a fallback has to take away/);
  const named = describeCheck({ supported: true, summary: summarizeLoad([{ average: 0.2, peak: 0.3 }]), estimate: 0.12, clips: 4, scene: 'Drop', bypassable: ['Bass', 'Pads'] });
  assert.match(named[0], /busiest scene, Drop/);
  assert.match(named[2], /filters of Bass, Pads are taken off/);
  const guessed = describeCheck({ supported: false, estimate: 0.12, clips: 1 });
  assert.match(guessed[0], /cannot measure/);
  assert.match(guessed[0], /an estimate, not a measurement/);
});

test('a promise that does not settle gives up with a message that says what took long, and one that does is left alone (issue 272)', async () => {
  await assert.rejects(withTimeout(new Promise(() => {}), 20, 'the clip Hall'), (error) => error.message.includes('the clip Hall') && error.message.includes('took longer than 0 s') && error.message.includes('given up'));
  assert.equal(await withTimeout(Promise.resolve(7), 1000, 'x'), 7);
  await assert.rejects(withTimeout(Promise.reject(new Error('no take')), 1000, 'x'), /no take/);
});

test('plain takes are made ready first, then patterns, then live chains, each once', () => {
  assert.deepEqual(loadOrder(['chain:a', 'kick', 'pattern:s:A', 'kick', 'snare', 'chain:b']), ['kick', 'snare', 'pattern:s:A', 'chain:a', 'chain:b']);
});
