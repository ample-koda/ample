import test from 'node:test';
import assert from 'node:assert/strict';
import { applyHunks, lineHunks } from '../koda-lines.mjs';

const song = ['composition "x" { ample 1  tempo 100 }', 'piano keys {', '  gain -9', '}', 'master master {', '  from keys.audio -> mix', '}'].join('\n');

test('an added block is one hunk at the right line, with nothing removed', () => {
  const proposed = [...song.split('\n').slice(0, 4), 'reverb room {', '  from keys.audio -> in', '}', ...song.split('\n').slice(4)].join('\n');
  assert.deepEqual(lineHunks(song, proposed), [{ start: 4, removed: 0, added: ['reverb room {', '  from keys.audio -> in', '}'] }]);
});

test('a changed line is one hunk that removes it and brings the new one', () => {
  const proposed = song.replace('  gain -9', '  gain -12');
  assert.deepEqual(lineHunks(song, proposed), [{ start: 2, removed: 1, added: ['  gain -12'] }]);
});

test('two changes far apart are two hunks, and applying them gives the proposed text back', () => {
  const proposed = song.replace('tempo 100', 'tempo 92').replace('from keys.audio -> mix', 'from keys.audio -> mix2');
  const hunks = lineHunks(song, proposed);
  assert.equal(hunks.length, 2);
  assert.equal(applyHunks(song, hunks), proposed);
});

test('no change gives no hunks, and a removal gives a hunk with no added lines', () => {
  assert.deepEqual(lineHunks(song, song), []);
  const proposed = song.split('\n').filter((l) => l !== '  gain -9').join('\n');
  assert.deepEqual(lineHunks(song, proposed), [{ start: 2, removed: 1, added: [] }]);
});
