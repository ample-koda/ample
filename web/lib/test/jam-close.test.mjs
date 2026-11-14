import test from 'node:test';
import assert from 'node:assert/strict';
import { discardedMessage, onCloseTake, playedCount } from '../jam-close.mjs';

test('closing the window while a take is recorded and notes were played discards the take (issue 244)', () => {
  assert.equal(onCloseTake({ recording: true, played: 3 }), 'discard');
  assert.equal(onCloseTake({ recording: true, played: 1 }), 'discard');
});

test('closing while recording is on but nothing was played has nothing to discard', () => {
  assert.equal(onCloseTake({ recording: true, played: 0 }), 'none');
});

test('closing without a recording changes nothing', () => {
  assert.equal(onCloseTake({ recording: false, played: 5 }), 'none');
  assert.equal(onCloseTake({ recording: false, played: 0 }), 'none');
  assert.equal(onCloseTake(), 'none');
  assert.equal(onCloseTake({}), 'none');
});

test('the notes of an unfinished take are the ones played and the ones still held', () => {
  assert.equal(playedCount({ notes: [{}, {}], open: new Map([[60, {}]]) }), 3);
  assert.equal(playedCount({ notes: [], open: new Map() }), 0);
  assert.equal(playedCount({ notes: [{}] }), 1);
  assert.equal(playedCount({ open: new Map([[1, {}], [2, {}]]) }), 2);
  assert.equal(playedCount(), 0);
});

test('a take of one held note is discarded too', () => {
  const rec = { on: true, notes: [], open: new Map([[64, { at: 0 }]]) };
  assert.equal(onCloseTake({ recording: rec.on, played: playedCount(rec) }), 'discard');
});

test('the message says how many notes were not added, in singular and plural', () => {
  assert.equal(discardedMessage(1), 'The window was closed during the recording, so the 1 unfinished note was not added to the clip.');
  assert.equal(discardedMessage(4), 'The window was closed during the recording, so the 4 unfinished notes were not added to the clip.');
});
