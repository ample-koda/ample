import test from 'node:test';
import assert from 'node:assert/strict';
import { afterRestore, afterSavedWrite, afterStagedWrite, mayWrite, needsRestore, playPlan } from '../seq-writes.mjs';

const opened = (text) => ({ snapshot: text, original: text, wroteLive: false });

test('a locked block or a read-only song is never written', () => {
  assert.equal(mayWrite(), true);
  assert.equal(mayWrite({ readOnly: false, locked: false }), true);
  assert.equal(mayWrite({ locked: true }), false);
  assert.equal(mayWrite({ readOnly: true }), false);
  assert.equal(mayWrite({ readOnly: true, locked: true }), false);
});

test('Play reuses a current take, builds a stale or missing one, and writes only when the block is not locked', () => {
  assert.equal(playPlan({ locked: false, hasTake: true, stale: false }), 'reuse');
  assert.equal(playPlan({ locked: false, hasTake: true, stale: true }), 'write-and-build');
  assert.equal(playPlan({ locked: false, hasTake: false, stale: true }), 'write-and-build');
  assert.equal(playPlan({ locked: false, hasTake: false, stale: false }), 'write-and-build');
});

test('on a locked block Play uses the audio there is and never writes (issue 246)', () => {
  assert.equal(playPlan({ locked: true, hasTake: true, stale: true }), 'reuse');
  assert.equal(playPlan({ locked: true, hasTake: true, stale: false }), 'reuse');
  assert.equal(playPlan({ locked: true, hasTake: false, stale: true }), 'build-only');
});

test('Play without Apply marks that it wrote, so Undo and back puts the saved text back (issue 235)', () => {
  const state = afterStagedWrite(opened('saved'), 'experiment');
  assert.deepEqual(state, { snapshot: 'experiment', original: 'saved', wroteLive: true });
  assert.equal(needsRestore(state), true);
  assert.deepEqual(afterRestore(state), { snapshot: 'saved', original: 'saved', wroteLive: false });
  assert.equal(needsRestore(afterRestore(state)), false);
});

test('nothing needs restoring when nothing was written', () => {
  assert.equal(needsRestore(opened('saved')), false);
});

test('a text that differs from the saved one needs restoring even without the mark', () => {
  assert.equal(needsRestore({ snapshot: 'other', original: 'saved', wroteLive: false }), true);
});

test('Save or Apply makes the written text the version Undo goes back to (issue 247)', () => {
  let state = afterStagedWrite(opened('opened with'), 'live preview');
  state = afterSavedWrite(state, 'live preview');
  assert.deepEqual(state, { snapshot: 'live preview', original: 'live preview', wroteLive: false });
  assert.equal(needsRestore(state), false);

  state = afterStagedWrite(state, 'a later staged change');
  assert.equal(needsRestore(state), true);
  const restored = afterRestore(state);
  assert.equal(restored.snapshot, 'live preview');   // the saved version, not the text the workspace was opened with
  assert.equal(restored.original, 'live preview');
});

test('the functions return new objects and leave the state they were given alone', () => {
  const state = opened('saved');
  const copy = { ...state };
  afterStagedWrite(state, 'x');
  afterSavedWrite(state, 'y');
  afterRestore(state);
  assert.deepEqual(state, copy);
});

test('other fields of the state are kept', () => {
  const state = { ...opened('saved'), embedded: true, path: 'a.seq' };
  assert.equal(afterStagedWrite(state, 'x').embedded, true);
  assert.equal(afterSavedWrite(state, 'x').path, 'a.seq');
  assert.equal(afterRestore(state).embedded, true);
});
