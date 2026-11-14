import test from 'node:test';
import assert from 'node:assert/strict';
import { scopeText, versionState } from '../seq-scope.mjs';

test('PAT names the pattern, SONG the whole sequencer, a range the bars (issue 248)', () => {
  assert.equal(scopeText({ mode: 'pattern', pattern: 'A', repeat: false }), 'Hears the pattern A · plays once');
  assert.equal(scopeText({ mode: 'song', repeat: true }), 'Hears the whole sequencer (without the effects after it) · repeats (only while you audition)');
  assert.equal(scopeText({ mode: 'pattern', pattern: 'A', range: { at: 5, bars: 4 }, repeat: true }), 'Hears bars 5 to 8 of the sequencer · repeats (only while you audition)');
});

test('the loop of the sequence itself is named apart from the audition loop', () => {
  const text = scopeText({ mode: 'song', repeat: false, savedLoop: { at: 3, bars: 2 } });
  assert.match(text, /plays once · saved loop of the sequence: bar 3 for 2$/);
});

test('a missing pattern name does not leave a gap', () => {
  assert.equal(scopeText({ mode: 'pattern', repeat: false }), 'Hears the pattern · plays once');
});

test('the version that is heard is named: preparing, previous, latest (issue 264)', () => {
  assert.equal(versionState({ playing: true, preparing: true, pending: false, stale: true }), 'Preparing the new version');
  assert.match(versionState({ playing: true, preparing: false, pending: true, stale: false }), /previous version: the new one comes in at the next return/);
  assert.match(versionState({ playing: true, preparing: false, pending: false, stale: true }), /previous version: your last edits are not built yet/);
  assert.equal(versionState({ playing: true, preparing: false, pending: false, stale: false }), 'Playing the latest version');
});

test('nothing playing and nothing changed says nothing, and unbuilt edits are said', () => {
  assert.equal(versionState({ playing: false, preparing: false, pending: false, stale: false }), '');
  assert.equal(versionState({ playing: false, preparing: false, pending: false, stale: true }), 'Edits not built yet');
});
