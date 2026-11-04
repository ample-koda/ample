import test from 'node:test';
import assert from 'node:assert/strict';
import { KEYS, LANGUAGES, missingIn, text } from '../koda-text.mjs';

test('every word of Koda exists in every language', () => {
  for (const lang of LANGUAGES) assert.deepEqual(missingIn(lang), [], `missing in ${lang}`);
  assert.ok(KEYS.length > 50);
});

test('a text is filled in and falls back to English for a key or a language that is missing', () => {
  assert.equal(text('nl', 'thought.looking', { x: 'piano' }), 'Koda denkt na over piano');
  assert.equal(text('en', 'thought.looking', { x: 'piano' }), 'Koda is thinking about piano');
  assert.equal(text('fr', 'menu.hide'), 'Hide Koda');
  assert.equal(text('nl', 'no.such.key'), 'no.such.key');
});

test('Koda does not call themself he or she in either language', () => {
  for (const lang of LANGUAGES) for (const key of KEYS) assert.doesNotMatch(text(lang, key), /\b(he|she|hij|zij)\b/i, `${lang} ${key}`);
});
