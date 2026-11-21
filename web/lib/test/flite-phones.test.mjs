import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { FLITE_PHONEMES, fliteProblem } from '../flite-phones.mjs';

test('the phoneme list of the panel is the table of the engine', () => {
  const source = readFileSync(new URL('../../../src/Ample.Speech/Flite/FlitePhoneTable.cs', import.meta.url), 'utf8');
  const plain = source.slice(source.indexOf('Plain = new'), source.indexOf('Vowels = new'));
  const keys = [...plain.matchAll(/\["([A-Z]+)"\]/g)].map((m) => m[1]);
  assert.deepEqual(FLITE_PHONEMES, keys);
  const vowels = [...source.slice(source.indexOf('Vowels = new')).matchAll(/"([A-Z]+)"/g)].map((m) => m[1]).slice(0, 16);
  assert.deepEqual(FLITE_PHONEMES.slice(0, 16), vowels);
});

test('good phonemes pass, in any case, with stress on vowels', () => {
  assert.equal(fliteProblem('HH AH0 L OW1  W ER1 L D'), null);
  assert.equal(fliteProblem('hh ah0 l ow1'), null);
  assert.equal(fliteProblem(''), null);
  assert.equal(fliteProblem('  \n '), null);
});

test('an unknown phoneme is named and the valid ones are listed', () => {
  const found = fliteProblem('HH XX1 L');
  assert.equal(found.reason, 'unknown');
  assert.equal(found.phoneme, 'XX1');
  assert.match(found.message, /'XX1'/);
  assert.match(found.message, /IY IH EH/);
});

test('stress rules: only 0 to 2 and only on vowels', () => {
  assert.equal(fliteProblem('AH3').reason, 'stress');
  assert.equal(fliteProblem('T1').reason, 'stress-on-consonant');
  assert.equal(fliteProblem('AH2'), null);
});
