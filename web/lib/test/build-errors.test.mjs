import test from 'node:test';
import assert from 'node:assert/strict';
import { collectBuildErrors, errorLineFor, liveMarks } from '../build-errors.mjs';

const lines = ['composition "x" { ample 1  tempo 100 }', 'sampler s at 1 for 4 {', '  file samples/kick.wav', '  gain 0', '}'];
const doc = { blocks: [{ name: 's', line: 1, parameters: [{ name: 'file', value: 'samples/kick.wav', line: 2 }, { name: 'gain', value: '0', line: 3 }] }] };

test('the line is the parameter the message names, otherwise the header of the block', () => {
  const block = { name: 's', line: 1, parameters: [{ name: 'file', value: 'samples/kick.wav', line: 2 }, { name: 'gain', value: '0', line: 3 }] };
  assert.equal(errorLineFor(block, "The sample 'samples/kick.wav' is not in the project."), 2);
  assert.equal(errorLineFor(block, "The parameter 'gain' is out of range."), 3);
  assert.equal(errorLineFor(block, 'The block cannot be rendered.'), 1);
  assert.equal(errorLineFor({ name: 'q', line: 7, parameters: [] }, 'The sequencer has no file'), 7);
});

test('only failed blocks are marked, with the message, its code and the text of the line', () => {
  const items = collectBuildErrors({ blocks: [{ block: 's', status: 'failed', message: "AMP5380: The sample 'samples/kick.wav' is not in the project." }, { block: 't', status: 'skipped' }, { Block: 'gone', Status: 'failed', Message: 'x' }] }, doc, lines);
  assert.deepEqual(Object.keys(items), ['s']);
  assert.deepEqual(items.s, { message: "AMP5380: The sample 'samples/kick.wav' is not in the project.", code: 'AMP5380', line: 2, text: '  file samples/kick.wav' });
});

test('a mark follows its line when lines move and goes away when the line is edited', () => {
  const items = collectBuildErrors({ blocks: [{ block: 's', status: 'failed', message: "'samples/kick.wav' missing" }] }, doc, lines);
  assert.deepEqual(Object.keys(liveMarks(items, lines)), ['2']);
  const moved = ['# new', ...lines];
  assert.deepEqual(Object.keys(liveMarks(items, moved)), ['3']);
  const edited = [...lines];
  edited[2] = '  file samples/snare.wav';
  assert.deepEqual(liveMarks(items, edited), {});
  assert.deepEqual(liveMarks({}, lines), {});
});

test('a mark follows its line to the nearest line with the same text, not to the first one', () => {
  const items = { b: { message: 'AMP1 x', code: 'AMP1', line: 12, text: '  fingerprint content' } };
  const text = Array.from({ length: 30 }, (_, i) => (i === 3 || i === 14 || i === 25 ? '  fingerprint content' : `line ${i}`));
  assert.deepEqual(Object.keys(liveMarks(items, text)), ['14']);   // moved down two lines; not line 3
});
