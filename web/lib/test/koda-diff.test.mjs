import test from 'node:test';
import assert from 'node:assert/strict';
import { describeChanges as describeAll, structuredHunks, textWith } from '../koda-diff.mjs';

// the block and isNew fields are for grouping (koda-groups.test.mjs); here the words and the kind are checked
const describeChanges = (a, b) => describeAll(a, b).map(({ block, isNew, ...rest }) => rest);

const demo = ['piano piano pinned@2.0 at 1 for 8 {', '  from chords.notes -> notes', '}', 'mixer mix {', '  from drums.audio -> in1', '  from piano.audio -> in3', '}'].join('\n');

test('a reverb put after the piano is told as a new block and a link that now comes from it', () => {
  const proposed = ['piano piano pinned@2.0 at 1 for 8 {', '  from chords.notes -> notes', '}', 'reverb piano_reverb {', '  from piano.audio -> in', '}', 'mixer mix {', '  from drums.audio -> in1', '  from piano_reverb.out -> in3', '}'].join('\n');
  assert.deepEqual(describeChanges(demo, proposed), [
    { type: 'add', text: 'New reverb piano_reverb, reads from piano', hunk: 0 },
    { type: 'link', text: 'mix now reads from piano_reverb.out into in3 (before: piano.audio)', hunk: 1 },
  ]);
});

test('a changed value, an added line and a removed block are told in words', () => {
  const proposed = demo.replace('pinned@2.0 at 1 for 8 {', 'pinned@2.0 at 1 for 8 {\n  gain -6').replace('  from drums.audio -> in1\n', '');
  const items = describeChanges(demo, proposed);
  assert.ok(items.some((i) => i.type === 'add' && i.text === 'piano: gain -6'));
  assert.ok(items.some((i) => i.type === 'remove' && /drums\.audio/.test(i.text)));
  assert.deepEqual(describeChanges(demo, demo), []);
  assert.deepEqual(describeChanges(demo, 'piano piano pinned@2.0 at 1 for 8 {\n  from chords.notes -> notes\n}'), [{ type: 'remove', text: 'Block mix removed', hunk: 0, optional: true }]);
});

test('a new block is one hunk on the lines of that block, also when its closing brace looks like the one before it', () => {
  const before = ['mixer mix {', '  from piano.audio -> in3', '}', 'master master {', '  from mix.mix -> mix', '}'].join('\n');
  const after = ['mixer mix {', '  from piano_reverb.out -> in3', '}', 'reverb piano_reverb {', '  from piano.audio -> in', '}', 'master master {', '  from mix.mix -> mix', '}'].join('\n');
  const hunks = structuredHunks(before, after);
  assert.deepEqual(hunks.find((h) => h.kind === 'block-added'), { start: 3, removed: 0, added: ['reverb piano_reverb {', '  from piano.audio -> in', '}'], kind: 'block-added', block: 'piano_reverb', type: 'reverb' });
  assert.deepEqual(hunks.find((h) => h.kind === 'lines'), { start: 1, removed: 1, added: ['  from piano_reverb.out -> in3'], kind: 'lines', block: 'mix' });
  assert.deepEqual(structuredHunks(before, before), []);
});

test('a block put after the one it follows, where the closing braces could be matched the wrong way, is still one block', () => {
  const before = ['mixer mix {', '  from piano.audio -> in3', '}'].join('\n');
  const after = ['mixer mix {', '  from piano_reverb.out -> in3', '}', 'reverb piano_reverb {', '  from piano.audio -> in', '}'].join('\n');
  const added = structuredHunks(before, after).filter((h) => h.kind === 'block-added');
  assert.equal(added.length, 1);
  assert.equal(added[0].added[0], 'reverb piano_reverb {');
  assert.equal(added[0].added.at(-1), '}');
});

test('a long value is told by its size, and a draft can be applied without some of its changes', () => {
  const notes = (n) => `  notes "${Array.from({ length: n }, (_, i) => `${i}:1:0.75:48:96`).join(' ')}"`;
  const before = ['chords chords {', notes(12), '}', 'duck duck {', '}', 'mixer mix {', '}'].join('\n');
  const after = ['chords chords {', notes(16), '}', 'mixer mix {', '}'].join('\n');
  const items = describeChanges(before, after);
  assert.deepEqual(items.map((i) => i.text), ['chords: notes changed (12 → 16 items)', 'Block duck removed']);
  assert.equal(items[1].optional, true);
  const hunks = structuredHunks(before, after);
  const duck = items.find((i) => i.optional).hunk;
  const kept = textWith(before, after, new Set(hunks.map((_, i) => i).filter((i) => i !== duck)));
  assert.ok(kept.includes('duck duck'));
  assert.ok(kept.includes(notes(16)));
  assert.equal(textWith(before, after, new Set(hunks.map((_, i) => i))), after);
  assert.equal(textWith(before, after, new Set()), before);
});
