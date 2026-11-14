import test from 'node:test';
import assert from 'node:assert/strict';
import { blockTargetKey, documentTarget, sameTarget, staleTargetMessage } from '../edit-target.mjs';

const songA = { path: 'a.ample', moduleStack: [], modulePath: null };
const songB = { path: 'b.ample', moduleStack: [], modulePath: null };
const moduleOfA = { path: 'a.ample', moduleStack: ['m'], modulePath: 'm.ample' };

test('the same song is the same target', () => {
  assert.equal(sameTarget(documentTarget(songA), documentTarget({ ...songA })), true);
});

test('another song is another target, so a queued edit made in A is not applied to B', () => {
  assert.equal(sameTarget(documentTarget(songA), documentTarget(songB)), false);
});

test('a module of the song is another target than the song itself', () => {
  assert.equal(sameTarget(documentTarget(songA), documentTarget(moduleOfA)), false);
  assert.equal(documentTarget(moduleOfA).kind, 'module');
  assert.equal(documentTarget(moduleOfA).path, 'm.ample');
});

test('the same module path inside another song is another target', () => {
  const moduleOfB = { ...moduleOfA, path: 'b.ample' };
  assert.equal(sameTarget(documentTarget(moduleOfA), documentTarget(moduleOfB)), false);
});

test('no open song never matches', () => {
  const none = documentTarget({ path: null, moduleStack: [], modulePath: null });
  assert.equal(sameTarget(none, none), false);
  assert.equal(sameTarget(null, documentTarget(songA)), false);
});

test('a block name that two songs share gives two keys', () => {
  const a = blockTargetKey(documentTarget(songA), 'shared');
  const b = blockTargetKey(documentTarget(songB), 'shared');
  assert.notEqual(a, b);
  assert.equal(a, blockTargetKey(documentTarget(songA), 'shared'));
});

test('the message names the document the edit belonged to', () => {
  assert.match(staleTargetMessage(documentTarget(songA)), /song a\.ample/);
  assert.match(staleTargetMessage(documentTarget(moduleOfA)), /module m\.ample/);
});
