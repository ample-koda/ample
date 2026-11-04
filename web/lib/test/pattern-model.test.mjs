import test from 'node:test';
import assert from 'node:assert/strict';
import { MAIN, addPattern, addSection, check, deletePattern, duplicatePattern, expandSong, formatPatterns, formatSong, moveSection, nameAtBar, parsePatterns, parseSong, removeSection, renamePattern, sectionNames, sectionRanges, setBars, setGrid, uniqueName } from '../pattern-model.mjs';

const text = 'verse=kick:x... snare:..x. / fill=kick:xxxx';

test('patterns and a song are read and written back as the same text', () => {
  const { patterns, problems } = parsePatterns(text);
  assert.deepEqual(problems, []);
  assert.deepEqual(patterns.map((p) => p.name), ['verse', 'fill']);
  assert.equal(formatPatterns(patterns), text);
  const { entries } = parseSong('verse*2 fill pattern');
  assert.deepEqual(entries, [{ name: 'verse', bars: 2 }, { name: 'fill', bars: 1 }, { name: 'pattern', bars: 1 }]);
  assert.equal(formatSong(entries), 'verse*2 fill pattern');
  assert.deepEqual(expandSong(entries), ['verse', 'verse', 'fill', 'pattern']);
  assert.deepEqual(parsePatterns('"verse=kick:x..."').patterns.map((p) => p.name), ['verse']);
});

test('names the engine owns, doubles and unreadable parts are reported like the engine reports them', () => {
  assert.equal(parsePatterns('pattern=kick:x... / beat-A=kick:x... / ok=kick:x... / ok=kick:.x.. / =kick:x... / nope').problems.length, 5);
  assert.equal(parseSong('verse*0 chorus*1000 x*y verse*2').entries.length, 1);
  assert.equal(parseSong('verse*0 chorus*1000 x*y verse*2').problems.length, 3);
});

test('the bar decides the section and the sequence repeats', () => {
  const { entries } = parseSong('a*2 b');
  assert.deepEqual([0, 1, 2, 3, 4, 5].map((i) => nameAtBar(entries, i)), ['a', 'a', 'b', 'a', 'a', 'b']);
  assert.equal(nameAtBar([], 9), MAIN);
  assert.deepEqual(sectionRanges(entries).map((r) => [r.name, r.start, r.bars]), [['a', 1, 2], ['b', 3, 1]]);
});

test('renaming follows the song, deleting removes the sections, and the engine names stay free', () => {
  const { patterns } = parsePatterns(text), { entries } = parseSong('verse*2 fill verse');
  const renamed = renamePattern(patterns, entries, 'verse', 'groove');
  assert.deepEqual(renamed.entries.map((e) => e.name), ['groove', 'fill', 'groove']);
  assert.equal(renamePattern(patterns, entries, 'verse', 'fill'), null);
  assert.equal(renamePattern(patterns, entries, 'verse', 'beat-A'), null);
  assert.equal(renamePattern(patterns, entries, 'verse', 'bad name'), null);
  const gone = deletePattern(patterns, entries, 'verse');
  assert.deepEqual(gone.patterns.map((p) => p.name), ['fill']);
  assert.deepEqual(gone.entries, [{ name: 'fill', bars: 1 }]);
});

test('adding, duplicating and the grid edits leave their arguments alone', () => {
  const { patterns } = parsePatterns(text), { entries } = parseSong('verse');
  const added = addPattern(patterns, entries, 'verse', 'kick:....');
  assert.equal(added.name, 'verse-2');
  assert.equal(patterns.length, 2);
  assert.equal(duplicatePattern(patterns, entries, 'verse', 'kick:x...').name, 'verse-copy');
  assert.equal(setGrid(patterns, 'fill', 'kick:....')[1].grid, 'kick:....');
  assert.equal(patterns[1].grid, 'kick:xxxx');
  assert.equal(uniqueName('beat-A', []), 'beat-A-2');
  assert.equal(uniqueName('  ', ['pattern']), 'pattern-2');
});

test('sections: add, move, remove and the repeat stay between 1 and 999', () => {
  let song = parseSong('a b c').entries;
  song = addSection(song, 'd', 3);
  assert.equal(formatSong(song), 'a b c d*3');
  song = moveSection(song, 3, 0);
  assert.equal(formatSong(song), 'd*3 a b c');
  song = removeSection(song, 1);
  assert.equal(formatSong(song), 'd*3 b c');
  assert.equal(setBars(song, 0, 0)[0].bars, 1);
  assert.equal(setBars(song, 0, 5000)[0].bars, 999);
  assert.deepEqual(moveSection(song, 9, 0), song);
});

test('a section that names no pattern is a problem and the names to choose from include the engine ones', () => {
  const { patterns } = parsePatterns(text);
  assert.deepEqual(check(patterns, parseSong('verse chorus').entries), ["the song names 'chorus', which is not a pattern"]);
  assert.deepEqual(sectionNames(patterns), [MAIN, 'verse', 'fill', 'beat-A', 'beat-B']);
});
