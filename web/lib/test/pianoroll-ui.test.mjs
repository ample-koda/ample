import { test } from 'node:test';
import assert from 'node:assert/strict';
import { drawPianoRollNote, sizePianoRollCanvas } from '../../app/pianoroll-draw.js';
import { pianoRollHit, pianoRollPoint } from '../../app/pianoroll-input.js';

test('short notes have a usable resize edge without losing their move area', () => {
  const notes = [{ at: 10, length: 4, pitch: 60, index: 0 }], xOf = (x) => x, yOf = () => 10;
  assert.equal(pianoRollHit(notes, 10, 12, 100, 14, xOf, yOf, 100, 6).edge, false);
  assert.equal(pianoRollHit(notes, 14, 12, 100, 14, xOf, yOf, 100, 6).edge, true);
});

test('canvas sizing uses the device scale while keeping its CSS dimensions', () => {
  const calls = [];
  const canvas = { style: {}, getContext: () => ({ setTransform: (...args) => calls.push(args) }) };
  sizePianoRollCanvas(canvas, 101, 52, 2);
  assert.deepEqual([canvas.width, canvas.height, canvas.style.width, canvas.style.height], [202, 104, '101px', '52px']);
  assert.deepEqual(calls, [[2, 0, 0, 2, 0, 0]]);
});

test('note glyph helper draws the supplied view model and restores alpha', () => {
  const calls = [], gradient = { addColorStop: (...args) => calls.push(['stop', ...args]) };
  const context = { globalAlpha: 1, createLinearGradient: () => gradient, fillRect: (...args) => calls.push(['rect', ...args]) };
  drawPianoRollNote(context, {}, { x: 4, y: 8, width: 20, height: 10 }, { highContrast: false, fill: '#abc', shade: '#789', alpha: 0.5, selected: false });
  assert.equal(context.globalAlpha, 1);
  assert.ok(calls.some(call => call[0] === 'rect' && call[1] === 4));
  assert.deepEqual(calls.filter(call => call[0] === 'stop').map(call => call.slice(1)), [[0, '#abc'], [1, '#789']]);
});

test('pointer mapping and note hit testing use CSS-space positions and the edge handle', () => {
  const canvas = { clientWidth: 200, clientHeight: 100, getBoundingClientRect: () => ({ left: 10, top: 20, width: 200, height: 100 }) };
  assert.deepEqual(pianoRollPoint(canvas, { clientX: 30, clientY: 50 }), { x: 20, y: 30 });
  const notes = [{ at: 0, length: 100, pitch: 60, index: 0 }];
  const args = [notes, 95, 15, 100, 20, at => at, pitch => pitch - 50, 100, 8];
  assert.equal(pianoRollHit(...args).edge, true);
  assert.equal(pianoRollHit(notes, 20, 15, 100, 20, at => at, pitch => pitch - 50, 100, 8).edge, false);
  assert.equal(pianoRollHit(notes, 150, 15, 100, 20, at => at, pitch => pitch - 50, 100, 8), null);
});
