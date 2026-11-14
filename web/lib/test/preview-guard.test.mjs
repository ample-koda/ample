import test from 'node:test';
import assert from 'node:assert/strict';
import { CHANGED_MESSAGE, captureOf, changedSince } from '../preview-guard.mjs';

test('nothing changed: the same song, text and range', () => {
  const captured = captureOf('a.amplet', 'text', { firstBar: 2, bars: 4 });
  assert.equal(changedSince(captured, 'a.amplet', 'text', { firstBar: 2, bars: 4 }), false);
  assert.equal(changedSince(captureOf('a.amplet', 'text', null), 'a.amplet', 'text', undefined), false);
});

test('a range chosen while the preview waited is a change (issue 71)', () => {
  const captured = captureOf('a.amplet', 'text', null);
  assert.equal(changedSince(captured, 'a.amplet', 'text', { firstBar: 3, bars: 2 }), true);
});

test('a range that was cleared or moved is a change', () => {
  const captured = captureOf('a.amplet', 'text', { firstBar: 2, bars: 4 });
  assert.equal(changedSince(captured, 'a.amplet', 'text', null), true);
  assert.equal(changedSince(captured, 'a.amplet', 'text', { firstBar: 3, bars: 4 }), true);
});

test('another song or another text is a change', () => {
  const captured = captureOf('a.amplet', 'text', null);
  assert.equal(changedSince(captured, 'b.amplet', 'text', null), true);
  assert.equal(changedSince(captured, 'a.amplet', 'text changed', null), true);
});

test('the captured range is a copy: changing the original afterwards does not move it', () => {
  const range = { firstBar: 2, bars: 4 };
  const captured = captureOf('a.amplet', 'text', range);
  range.bars = 8;
  assert.equal(changedSince(captured, 'a.amplet', 'text', range), true);
});

test('the message tells the person to build the preview again', () => {
  assert.match(CHANGED_MESSAGE, /Build the preview again/);
});
