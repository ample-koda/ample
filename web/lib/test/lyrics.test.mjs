import test from 'node:test';
import assert from 'node:assert/strict';
import { lyricChecks, lyricsText, parseLyrics } from '../../app/lyrics-model.js';

const node = (extra = {}) => ({ parts: [{ at: 9, length: 8, name: 'chorus' }], params: { first5s: 'check' }, ...extra });

test('lyrics parse into bar and text and keep what they cannot read apart', () => {
  const r = parseLyrics('[bar 9]  Regen op de ruiten\n\n[bar 11.5] en ik zing\nnot a line\n');
  assert.deepEqual(r.rows, [{ bar: 9, text: 'Regen op de ruiten' }, { bar: 11.5, text: 'en ik zing' }]);
  assert.deepEqual(r.bad, [4]);
  assert.equal(lyricsText(r.rows), '[bar 9] Regen op de ruiten\n[bar 11.5] en ik zing\n');
});

test('checks find order, placement, empty and long lines, fast words and the first five seconds', () => {
  const rows = [{ bar: 12, text: 'late' }, { bar: 10, text: '' }, { bar: 10, text: 'x'.repeat(70) }, { bar: 20, text: 'out' }];
  const msgs = lyricChecks(rows, node(), 120).map((c) => `${c.sev}:${c.msg}`);
  assert.ok(msgs.some((m) => m.startsWith('error:') && m.includes('comes before')));
  assert.ok(msgs.some((m) => m.includes('has no text')));
  assert.ok(msgs.some((m) => m.includes('same bar')));
  assert.ok(msgs.some((m) => m.includes('is long')));
  assert.ok(msgs.some((m) => m.includes('after the lyrics part ends')));
  assert.ok(msgs.some((m) => m.includes('first5s check: the first line starts')));
});

test('a fast line is flagged and a good song passes', () => {
  const good = lyricChecks([{ bar: 9, text: 'one two' }, { bar: 11, text: 'three four' }], node(), 92).filter((c) => c.sev !== 'info');
  assert.deepEqual(good, []);
  const fast = lyricChecks([{ bar: 9, text: 'a b c d e f g h i j k l' }, { bar: 9.25, text: 'x' }], node({ params: {} }), 120);
  assert.ok(fast.some((c) => c.msg.includes('words per second')));
  assert.ok(lyricChecks([], node(), 120).some((c) => c.msg.includes('no lyric lines')));
});

test('a section tag is a row of its own without a bar, is kept in order and does not count for the checks', async () => {
  const { sortByTime, isSorted, timedRows } = await import('../../app/lyrics-model.js');
  const r = parseLyrics('[Verse]\n[bar 3] two\n[Chorus]\n[bar 1] one\n');
  assert.deepEqual(r.rows, [{ tag: true, text: '[Verse]' }, { bar: 3, text: 'two' }, { tag: true, text: '[Chorus]' }, { bar: 1, text: 'one' }]);
  assert.equal(isSorted(r.rows), false);
  assert.equal(lyricsText(sortByTime(r.rows)), '[Chorus]\n[bar 1] one\n[Verse]\n[bar 3] two\n');
  assert.equal(timedRows(r.rows).length, 2);
  assert.ok(!lyricChecks(sortByTime(r.rows), node({ parts: [{ at: 1, length: 8, name: 'x' }] }), 120).some((c) => c.sev === 'error'));
});
