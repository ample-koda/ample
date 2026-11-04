import test from 'node:test';
import assert from 'node:assert/strict';
import { variantDrafts } from '../koda-variants.mjs';

const base = ['piano piano pinned@2.0 at 1 for 8 {', '}', 'mixer mix {', '  from piano.audio -> in1', '}', ''].join('\n');
const proposed = [
  'piano piano pinned@2.0 at 1 for 8 {', '}',
  'piano warm_piano pinned@2.0 at 1 for 8 {', '}',
  'piano warm_violin pinned@2.0 at 1 for 8 {', '}',
  'mixer mix {', '  from piano.audio -> in1', '}', '',
].join('\n');
const variants = [
  { name: 'Warm piano', summary: 'soft', blocks: ['warm_piano'], listen: 'warm_piano' },
  { name: 'Violin', summary: 'strings', blocks: ['warm_violin'] },
];

test('every variant is the proposal with only its own blocks, and says what to listen to', () => {
  const [a, b] = variantDrafts(base, proposed, variants);
  assert.match(a.text, /warm_piano/);
  assert.doesNotMatch(a.text, /warm_violin/);
  assert.match(b.text, /warm_violin/);
  assert.doesNotMatch(b.text, /warm_piano/);
  assert.equal(a.listen, 'warm_piano');
  assert.equal(b.listen, 'warm_violin');   // the last block when none is given
  assert.deepEqual(a.summary.map((c) => c.block), ['warm_piano']);
});

test('a change that belongs to no variant is in all of them', () => {
  const shared = proposed.replace('from piano.audio -> in1', 'from piano.audio -> in1\n  from piano.audio -> in2');
  const drafts = variantDrafts(base, shared, variants);
  assert.ok(drafts.every((d) => /in2/.test(d.text)));
});

test('the lines that take a variant into the mixer stay with that variant, also when they sit next to each other', () => {
  const both = proposed.replace('from piano.audio -> in1', 'from piano.audio -> in1\n  from warm_piano.audio -> in2\n  from warm_violin.audio -> in3');
  const [a, b] = variantDrafts(base, both, variants);
  assert.match(a.text, /from warm_piano\.audio -> in2/);
  assert.doesNotMatch(a.text, /warm_violin\.audio/);
  assert.match(b.text, /from warm_violin\.audio -> in3/);
  assert.doesNotMatch(b.text, /warm_piano\.audio/);
  assert.ok(a.summary.every((c) => !/warm_violin/.test(c.text)));
});
