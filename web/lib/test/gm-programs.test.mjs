import test from 'node:test';
import assert from 'node:assert/strict';
import { GM_PROGRAMS, gmProgramName, parseGmProgram } from '../gm-programs.mjs';

test('there are 128 programs and 42 is the cello', () => {
  assert.equal(GM_PROGRAMS.length, 128);
  assert.equal(gmProgramName(42), 'Cello');
  assert.equal(gmProgramName(40), 'Violin');
  assert.equal(gmProgramName(56), 'Trumpet');
  assert.equal(gmProgramName(200), '');
});

test('a number, a name or a unique part of a name gives the program', () => {
  assert.equal(parseGmProgram('42'), 42);
  assert.equal(parseGmProgram(' cello '), 42);
  assert.equal(parseGmProgram('42 · Cello'), 42);
  assert.equal(parseGmProgram('tenor sax'), 66);
  assert.equal(parseGmProgram('harpsichord'), 6);
  assert.equal(parseGmProgram('harp'), null);   // harpsichord and orchestral harp
  assert.equal(parseGmProgram('200'), null);
  assert.equal(parseGmProgram('piano'), null);
  assert.equal(parseGmProgram(''), null);
});
