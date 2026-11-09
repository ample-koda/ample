import test from 'node:test';
import assert from 'node:assert/strict';
import { isQuoted, quoteLiteral, unquoteLiteral } from '../string-literal.mjs';

test('a quoted string is recognised, an unquoted value or an unclosed one is not', () => {
  assert.equal(isQuoted('"Covers/One more light/x.pdf"'), true);
  assert.equal(isQuoted('  "a"  '), true);
  assert.equal(isQuoted('""'), true);
  assert.equal(isQuoted('saw'), false);
  assert.equal(isQuoted('"'), false);
  assert.equal(isQuoted('"abc\\"'), false);   // the last quote is escaped: the string is not closed
  assert.equal(isQuoted('"abc\\\\"'), true);   // an escaped backslash, then the closing quote
  assert.equal(isQuoted(undefined), false);
});

test('the text of a property is shown without quotes and with its escapes resolved', () => {
  assert.equal(unquoteLiteral('"Covers/One more light/x.pdf"'), 'Covers/One more light/x.pdf');
  assert.equal(unquoteLiteral('"say \\"hi\\""'), 'say "hi"');
  assert.equal(unquoteLiteral('"line one\\nline two\\tend"'), 'line one\nline two\tend');
  assert.equal(unquoteLiteral('"C:\\\\Temp"'), 'C:\\Temp');
  assert.equal(unquoteLiteral('"a\\qb"'), 'aqb');   // any other backslash sequence stands for the character itself
  assert.equal(unquoteLiteral('saw'), 'saw');
});

test('saving puts the quotes and the escapes back, and the round trip keeps the text', () => {
  assert.equal(quoteLiteral('Covers/x.pdf'), '"Covers/x.pdf"');
  assert.equal(quoteLiteral('say "hi"'), '"say \\"hi\\""');
  assert.equal(quoteLiteral('one\ntwo\r\n\tend'), '"one\\ntwo\\n\\tend"');
  assert.equal(quoteLiteral('C:\\Temp'), '"C:\\\\Temp"');
  for (const text of ['plain', 'with "quotes"', 'back\\slash', 'two\nlines\tand tab', '']) assert.equal(unquoteLiteral(quoteLiteral(text)), text);
});
