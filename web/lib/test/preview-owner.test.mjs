import test from 'node:test';
import assert from 'node:assert/strict';
import { previewOwnership } from '../preview-owner.mjs';

test('the window that starts a preview owns it, and a second window takes it over', () => {
  const preview = previewOwnership();
  const a = { name: 'A' };
  const b = { name: 'B' };
  assert.equal(preview.claim(a), null);
  assert.equal(preview.owns(a), true);
  assert.equal(preview.claim(b), a);   // A is told it lost the preview
  assert.equal(preview.owns(a), false);
  assert.equal(preview.owns(b), true);
});

test('claiming again by the same window changes nothing', () => {
  const preview = previewOwnership();
  const a = {};
  preview.claim(a);
  assert.equal(preview.claim(a), null);
  assert.equal(preview.owns(a), true);
});

test('only the owner can give the preview up, so closing another window leaves it playing', () => {
  const preview = previewOwnership();
  const a = {};
  const b = {};
  preview.claim(a);
  preview.release(b);   // B never owned it
  assert.equal(preview.owns(a), true);
  preview.release(a);
  assert.equal(preview.owns(a), false);
});

test('nobody owns before a preview starts', () => {
  const preview = previewOwnership();
  assert.equal(preview.owns({}), false);
  assert.equal(preview.owns(null), false);
});
