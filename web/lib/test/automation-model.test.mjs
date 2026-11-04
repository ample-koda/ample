import test from 'node:test';
import assert from 'node:assert/strict';
import { AUTOMATABLE, automationValueAt, liveSupported, previewWindow } from '../automation-model.mjs';

test('a curve holds its first value before the first point and its last value after the last point', () => {
  const points = [{ bar: 5, value: -20 }, { bar: 9, value: -10 }];
  assert.equal(automationValueAt(points, 1), -20);
  assert.equal(automationValueAt(points, 5), -20);
  assert.equal(automationValueAt(points, 7), -15);
  assert.equal(automationValueAt(points, 9), -10);
  assert.equal(automationValueAt(points, 40), -10);
  assert.equal(automationValueAt([], 3), null);
  assert.equal(automationValueAt([{ bar: 9, value: 1 }, { bar: 5, value: 3 }], 7), 2);   // unsorted points are sorted first
});

test('a logarithmic axis is joined in the log domain: half way between 100 and 10000 Hz is 1000 Hz', () => {
  const points = [{ bar: 1, value: 100 }, { bar: 3, value: 10000 }];
  assert.ok(Math.abs(automationValueAt(points, 2, { log: true }) - 1000) < 1e-6);
  assert.equal(automationValueAt(points, 2), 5050);
});

test('the filter resonance is automatable in percent, but not run live', () => {
  assert.deepEqual(AUTOMATABLE.filter.resonance, { min: 0, max: 100, unit: '%' });
  assert.equal(liveSupported('filter', 'resonance'), false);
});

test('only the effects and parameters the browser can run live are offered Live', () => {
  assert.equal(liveSupported('volume', 'gain'), true);
  assert.equal(liveSupported('filter', 'cutoff'), true);
  assert.equal(liveSupported('compressor', 'threshold'), true);
  assert.equal(liveSupported('reverb', 'mix'), true);
  assert.equal(liveSupported('pan', 'pan'), false);
  assert.equal(liveSupported('flanger', 'mix'), false);
  assert.equal(liveSupported('filter', 'gain'), false);
});

test('the preview window is centred on the bar and kept inside the song', () => {
  assert.deepEqual(previewWindow(20, 46), { firstBar: 16, bars: 8 });
  assert.deepEqual(previewWindow(1, 46), { firstBar: 1, bars: 8 });
  assert.deepEqual(previewWindow(46, 46), { firstBar: 39, bars: 8 });
  assert.deepEqual(previewWindow(3, 5), { firstBar: 1, bars: 5 });
  assert.deepEqual(previewWindow(10, undefined, 4), { firstBar: 8, bars: 4 });
});
