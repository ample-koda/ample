import test from 'node:test';
import assert from 'node:assert/strict';
import { formatAuto, parseAuto, unreadableAuto } from '../automation-model.mjs';

// The editor wiped valid curves: auto(1:0, 4.5:-6, 9:-60) was not read, opened as a flat line, and Apply replaced it (bug 12).
// The server reads decimal bars, seconds and a plus sign (Automation.cs ParseSamples).
test('a curve with a decimal bar or a plus sign is read and written back as it was', () => {
  const points = parseAuto('auto(1:0, 4.5:-6, 9:-60)');
  assert.deepEqual(points, [{ bar: 1, value: 0 }, { bar: 4.5, value: -6 }, { bar: 9, value: -60 }]);
  assert.equal(formatAuto(points), 'auto(1:0, 4.5:-6, 9:-60)');
  assert.deepEqual(parseAuto('auto(1:+3, 2:-3 dB)'), [{ bar: 1, value: 3 }, { bar: 2, value: -3 }]);
  assert.equal(formatAuto([{ bar: 3, value: 2 }, { bar: 1.25, value: 1 }]), 'auto(1.25:1, 3:2)');
});

test('a curve with a point in seconds cannot be shown on the bars: the editor says so instead of opening a flat line', () => {
  assert.equal(parseAuto('auto(1:0, 12s:-6)'), null);
  assert.equal(unreadableAuto('auto(1:0, 12s:-6)'), true);
  assert.equal(unreadableAuto('auto(1:0, 4.5:-6)'), false);
  assert.equal(unreadableAuto('-6 dB'), false);   // a plain value opens with starting points, as before
  assert.equal(unreadableAuto('lfo(4, -12, 0)'), false);
});
