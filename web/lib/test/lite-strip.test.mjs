import test from 'node:test';
import assert from 'node:assert/strict';
import { hueOf, neighbourChips, chipLabel, chipsSignature, pushBack, popBack, STRIP_WINDOW_BARS } from '../lite-strip.mjs';

const clip = (id, at, length = 2, title = id) => ({ id, title, tpl: 'midi-clip', parts: [{ at, length }] });
const nodes = [clip('a', 1), clip('b', 3), clip('c', 5), clip('far', 40), { id: 'gm1', title: 'gm1', tpl: 'gm', parts: [] }, clip('other', 4)];
const links = [{ from: 'a', to: 'gm1' }, { from: 'b', to: 'gm1' }, { from: 'c', to: 'gm1' }, { from: 'far', to: 'gm1' }, { from: 'other', to: 'gm2' }];

test('the strip shows the current clip and its placed neighbours near it, sorted by bar', () => {
  const chips = neighbourChips(nodes, links, 'b');
  assert.deepEqual(chips.map((c) => c.id), ['a', 'b', 'other', 'c']);   // lanes do not matter: AMPLE has one lane per block
  assert.deepEqual(chips.map((c) => c.current), [false, true, false, false]);
  assert.ok(!chips.some((c) => c.id === 'far'), 'a clip outside the window is left out');
});

test('the window is a few bars either side of the current clip', () => {
  assert.equal(STRIP_WINDOW_BARS, 8);
  assert.ok(neighbourChips(nodes, links, 'c', 40).some((c) => c.id === 'far'));
  assert.ok(neighbourChips(nodes, links, 'c', 30).every((c) => c.id !== 'far'));
  assert.ok(neighbourChips(nodes, links, 'far', 8).every((c) => c.id === 'far'));
});

test('links do not matter, an unplaced clip is left out, and an unknown clip gives nothing', () => {
  assert.deepEqual(neighbourChips(nodes, [], 'a').map((c) => c.id), ['a', 'b', 'other', 'c']);
  assert.deepEqual(neighbourChips([...nodes, { id: 'loose', title: 'loose', tpl: 'midi-clip', parts: [] }], links, 'a').map((c) => c.id), ['a', 'b', 'other', 'c']);
  assert.deepEqual(neighbourChips([{ id: 'x', title: 'x', tpl: 'midi-clip', parts: [] }], [], 'x').map((c) => c.id), ['x']);
  assert.deepEqual(neighbourChips(nodes, links, 'nope'), []);
});

test('a clip with several parts gives one chip per part', () => {
  const multi = [{ id: 'm', title: 'm', tpl: 'midi-clip', parts: [{ at: 1, length: 1 }, { at: 9, length: 1 }] }, { id: 'n', title: 'n', tpl: 'midi-clip', parts: [{ at: 5, length: 1 }] }];
  const chips = neighbourChips(multi, [], 'm');
  assert.deepEqual(chips.map((c) => `${c.id}@${c.at}`), ['m@1', 'n@5', 'm@9']);
});

test('colour is stable per id, labels carry the bars, the signature changes with a rename', () => {
  assert.equal(hueOf('kick'), hueOf('kick')); assert.notEqual(hueOf('kick'), hueOf('snare'));
  assert.ok(hueOf('anything') >= 0 && hueOf('anything') < 360);
  const [a, b] = neighbourChips(nodes, links, 'b');
  assert.equal(chipLabel(a), 'a · bar 1-2'); assert.equal(chipLabel({ title: 'x', at: 4, length: 1 }), 'x · bar 4');
  const renamed = nodes.map((n) => (n.id === 'a' ? { ...n, title: 'Verse' } : n));
  assert.notEqual(chipsSignature(neighbourChips(renamed, links, 'b')), chipsSignature([a, b]));
});

test('the back stack pops the last clip, ignores repeats and is capped', () => {
  let s = pushBack([], 'a'); s = pushBack(s, 'a'); s = pushBack(s, 'b');
  assert.deepEqual(s, ['a', 'b']);
  const p = popBack(s); assert.equal(p.id, 'b'); assert.deepEqual(p.stack, ['a']);
  assert.deepEqual(popBack([]), { id: null, stack: [] });
  let big = []; for (let i = 0; i < 30; i++) big = pushBack(big, `c${i}`);
  assert.equal(big.length, 20); assert.equal(big[19], 'c29');
});
