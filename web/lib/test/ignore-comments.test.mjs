import test from 'node:test';
import assert from 'node:assert/strict';
import { withoutSilenced } from '../ignore-comments.mjs';

const diag = (line, code, severity = 3) => ({ range: { start: { line, character: 0 }, end: { line, character: 1 } }, severity, code, message: 'x' });

test('a comment at the end of a line silences the warning or note with that code on that line only', () => {
  const text = 'a   # ample-ignore AMP2086\nb\n';
  const left = withoutSilenced([diag(0, 'AMP2086'), diag(0, 'AMP2089', 2), diag(1, 'AMP2086')], text);
  assert.deepEqual(left.map((d) => d.code + d.range.start.line), ['AMP20890', 'AMP20861']);
});

test('a comment line above silences the next line (a block header cannot end with a comment), several codes can be named', () => {
  const text = '# ample-ignore AMP2086, amp2089\nheader\nnext\n';
  const left = withoutSilenced([diag(1, 'AMP2086'), diag(1, 'AMP2089', 2), diag(2, 'AMP2086')], text);
  assert.deepEqual(left.map((d) => d.range.start.line), [2]);
});

test('an error is never silenced, and a # inside a string or a word is not a comment', () => {
  const text = 'x   # ample-ignore AMP2082\nname="a # ample-ignore AMP2086"\nword#ample-ignore AMP2086\n';
  const left = withoutSilenced([diag(0, 'AMP2082', 1), diag(1, 'AMP2086'), diag(2, 'AMP2086')], text);
  assert.equal(left.length, 3);
});
