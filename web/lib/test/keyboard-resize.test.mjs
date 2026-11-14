import test from 'node:test';
import assert from 'node:assert/strict';
import { resizedByKey } from '../keyboard-resize.mjs';

const o = { min: 160, max: 560, grow: 'ArrowRight', shrink: 'ArrowLeft' };

test('the grow key makes the panel bigger by one step and the shrink key smaller (issue 123)', () => {
  assert.equal(resizedByKey(300, 'ArrowRight', o), 324);
  assert.equal(resizedByKey(300, 'ArrowLeft', o), 276);
});

test('Shift moves four steps', () => {
  assert.equal(resizedByKey(300, 'ArrowRight', { ...o, shift: true }), 396);
  assert.equal(resizedByKey(300, 'ArrowLeft', { ...o, shift: true }), 204);
});

test('the size stays between the least and the most', () => {
  assert.equal(resizedByKey(550, 'ArrowRight', o), 560);
  assert.equal(resizedByKey(170, 'ArrowLeft', o), 160);
  assert.equal(resizedByKey(300, 'Home', o), 160);
  assert.equal(resizedByKey(300, 'End', o), 560);
});

test('another key does not resize, and the other arrows do nothing', () => {
  assert.equal(resizedByKey(300, 'a', o), null);
  assert.equal(resizedByKey(300, 'Enter', o), null);
  assert.equal(resizedByKey(300, 'ArrowUp', o), null);
  assert.equal(resizedByKey(300, 'ArrowDown', o), null);
});

test('a fractional size is rounded and the step can be changed', () => {
  assert.equal(resizedByKey(300.4, 'ArrowRight', { ...o, step: 10 }), 310);
  assert.equal(resizedByKey(300, 'ArrowUp', { min: 120, max: 800, grow: 'ArrowUp', shrink: 'ArrowDown' }), 324);
});
