// Step 4 of plan/live-preview/note-origin.md: the notes of a chain light the source notes they come from, per user of the source.
import test from 'node:test';
import assert from 'node:assert/strict';
import { moveMarks, originMarks, playbackMarks, sourceToken } from '../listen-highlight.mjs';

const SONG = [
  'composition "x" { ample 1  tempo 100  master-seed 1 }',
  '',
  'midi-clip theme {',
  '  melody "c4:1 r:1 d4:1 e4:1"',
  '}',
  '',
  'midi-clip high at 1 for auto {',
  '  notes theme.notes |> transpose(+12)',
  '}',
  '',
  'midi-clip low at 1 for auto {',
  '  notes theme.notes |> transpose(-12) |> shift(+1beat)',
  '}',
  '',
].join('\n');
const word = (text, w) => { const from = text.indexOf(w); return { from, to: from + w.length }; };
const B = 1e6;
// the notes of `high`: c4 at beat 0, d4 at beat 2, e4 at beat 3 (the rest takes no note)
const HIGH = [{ at: 0, len: B, pitch: 72, source: 'theme', index: 0, lap: 0 }, { at: 2 * B, len: B, pitch: 74, source: 'theme', index: 1, lap: 0 }, { at: 3 * B, len: B, pitch: 76, source: 'theme', index: 2, lap: 0 }];
const spans = (marks) => marks.map(({ from, to }) => SONG.slice(from, to));

test('a transposed chain lights the source note that sounds, at the beat it sounds', () => {
  assert.deepEqual(spans(originMarks({ text: SONG, user: 'high', origins: HIGH, songBar: 1 })), ['c4:1']);
  assert.deepEqual(spans(originMarks({ text: SONG, user: 'high', origins: HIGH, songBar: 1.25 })), [], 'the rest sounds nothing');
  assert.deepEqual(spans(originMarks({ text: SONG, user: 'high', origins: HIGH, songBar: 1.5 })), ['d4:1']);
  assert.deepEqual(spans(originMarks({ text: SONG, user: 'high', origins: HIGH, songBar: 1.75 })), ['e4:1']);
});

test('a chain that cuts notes away keeps the place of the survivors in the source', () => {
  const cut = [{ at: 0, len: B, pitch: 76, source: 'theme', index: 2, lap: 0 }];   // only the third note survived a bars() or where()
  assert.deepEqual(spans(originMarks({ text: SONG, user: 'high', origins: cut, songBar: 1 })), ['e4:1']);
  assert.deepEqual(originMarks({ text: SONG, user: 'high', origins: cut, songBar: 1.5 }), []);
});

test('a repeat marks the same source note again with its lap, and says which user it is', () => {
  const repeated = [...HIGH, { at: 4 * B, len: B, pitch: 72, source: 'theme', index: 0, lap: 1 }];
  const marks = originMarks({ text: SONG, user: 'high', origins: repeated, songBar: 2 });
  assert.deepEqual(marks.map((m) => [SONG.slice(m.from, m.to), m.user, m.lap]), [['c4:1', 'high', 1]]);
});

test('two users of one source light the same note, each with its own user', () => {
  const low = [{ at: 1 * B, len: B, pitch: 48, source: 'theme', index: 0, lap: 0 }];   // shifted a beat: c4 sounds at beat 1 here, at beat 0 in high
  const marks = playbackMarks({ text: SONG, target: 'theme', songBar: 1.25, active: ['high', 'low'], origins: { high: HIGH, low } });
  const lit = marks.filter((m) => m.cls === 'lp-now' && SONG.slice(m.from, m.to) === 'c4:1');
  assert.deepEqual(lit.map((m) => m.user).sort(), ['low'], 'at beat 1 only low plays c4');
  const both = playbackMarks({ text: SONG, target: 'theme', songBar: 1, active: ['high', 'low'], origins: { high: HIGH, low: [{ ...low[0], at: 0 }] } });
  assert.deepEqual(both.filter((m) => m.user).map((m) => m.user).sort(), ['high', 'low']);
});

test('a source with literal notes is read note by note; a chord list or a chain gives no token', () => {
  const text = 'midi-clip lit {\n  notes "1:0:1:60:100 1:1:1:62:100"\n}\n\nmidi-clip chords1 {\n  chords Am F\n}\n\nmidi-clip chain at 1 for auto {\n  notes lit.notes |> transpose(+1)\n}\n';
  const token = sourceToken(text, 'lit', 1);
  assert.equal(text.slice(token.from, token.to), '1:1:1:62:100');
  assert.equal(sourceToken(text, 'chords1', 0), null);
  assert.equal(sourceToken(text, 'chain', 0), null);
  assert.equal(sourceToken(text, 'lit', 5), null);
  assert.equal(sourceToken(text, 'missing', 0), null);
});

test('a note an operator made has no source note and marks nothing', () => {
  assert.deepEqual(originMarks({ text: SONG, user: 'high', origins: [{ at: 0, len: B, pitch: 60, source: null, index: null, lap: null }], songBar: 1 }), []);
});

test('the source is edited while it plays: the mark moves with its line, an edited line loses it', () => {
  const marks = originMarks({ text: SONG, user: 'high', origins: HIGH, songBar: 1 });
  const moved = SONG.replace('midi-clip theme {', '# a new comment line\nmidi-clip theme {');
  const onMoved = moveMarks(marks, SONG, moved);
  assert.equal(onMoved.length, 1);
  assert.equal(moved.slice(onMoved[0].from, onMoved[0].to), 'c4:1');
  assert.equal(onMoved[0].user, 'high', 'the user survives the move');
  const edited = SONG.replace('melody "c4:1 r:1 d4:1 e4:1"', 'melody "c4:2 r:1 d4:1 e4:1"');
  assert.deepEqual(moveMarks(marks, SONG, edited), []);
});

test('without origins nothing changes in playbackMarks', () => {
  const a = playbackMarks({ text: SONG, target: 'high', songBar: 1.25, active: [] });
  const b = playbackMarks({ text: SONG, target: 'high', songBar: 1.25, active: [], origins: {} });
  assert.deepEqual(a, b);
  assert.ok(a.some((m) => m.cls === 'lp-line'), 'the line of the chain is still marked');
});

test('the token mapping agrees with where the note really is', () => {
  const w = word(SONG, 'd4:1');
  assert.deepEqual(sourceToken(SONG, 'theme', 1), w);
});
