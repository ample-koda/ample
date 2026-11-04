import test from 'node:test';
import assert from 'node:assert/strict';
import { compactMidiNotes, expandMidiNotes, isCompactMidiNotes } from '../expanded-midi-notes.js';

test('unscoped tie suffix survives expanded notes without inventing routing', () => {
  const compact = '1:3:1:60:96;tie=4 2:0:1:60:96;tie=4';
  assert.equal(compactMidiNotes(expandMidiNotes(compact, 1_000_000), 1_000_000), compact);
  assert.equal(isCompactMidiNotes('1:0:1:60:96;tie=4;tie=4', 1_000_000), false);
});

test('expanded named note fields retain exact MIDI ticks and routing', () => {
  const compact = '18:22:72:121:96:5:0:60:2 18:94:26:72:121:5:0:60:2';
  const expanded = expandMidiNotes(compact, 480);
  assert.ok(expanded);
  assert.match(expanded, /start 22\n    length 72/);
  assert.equal(compactMidiNotes(expanded, 480), compact);
});

test('comments are kept in expanded text but do not alter the built note value', () => {
  const expanded = 'notes {\n  # phrase A\n  note {\n    # onset\n    bar 1\n    start 0\n    length 480\n    pitch 60\n    velocity 96\n  }\n}';
  assert.equal(compactMidiNotes(expanded, 480), '1:0:480:60:96');
  assert.equal(compactMidiNotes(expanded.replace('length 480', 'length -1'), 480), null);
});

test('Queen-sized generated note text stays bounded and valid without per-note DOM nodes', () => {
  const compact = Array.from({ length: 2145 }, (_, i) => `${1 + Math.floor(i / 100)}:${(i % 4) * 120}:120:${36 + (i % 60)}:96:0:0:0`).join(' ');
  const expanded = expandMidiNotes(compact, 480);
  assert.ok(expanded);
  assert.ok(expanded.length < 1_000_000);
  assert.equal(compactMidiNotes(expanded, 480), compact);
  assert.equal(isCompactMidiNotes(compact, 480), true);
});
