import test from 'node:test';
import assert from 'node:assert/strict';
import { signatureOf, staleBlocks } from '../build-stale.mjs';

const block = (name, type, froms = [], extra = {}) => ({ name, type, class: '', version: '', flags: [], freeze: '', parts: [], parameters: [], froms: froms.map((f) => ({ block: f, output: 'out', input: 'in', disabled: false })), ...extra });
const song = (...blocks) => ({ project: { tempo: 90 }, settings: [], uses: [], blocks });
const base = () => song(block('drums', 'midi'), block('bass', 'midi'), block('kit', 'sampler', ['drums']), block('amp', 'effect', ['bass']), block('mix', 'mixer', ['kit', 'amp']), block('master', 'master', ['mix']));

test('a changed block makes only itself and the blocks that read it out of date', () => {
  const before = base(), after = base();
  after.blocks[0].parameters = [{ name: 'notes', value: '1:0:1:60:96' }];
  const stale = staleBlocks(signatureOf(before), before, after);
  assert.deepEqual([...stale].sort(), ['drums', 'kit', 'master', 'mix']);   // bass and amp keep their build
});

test('a change in an unrelated branch leaves the other branch built', () => {
  const before = base(), after = base();
  after.blocks[3].parameters = [{ name: 'gain', value: '3' }];
  assert.deepEqual([...staleBlocks(signatureOf(before), before, after)].sort(), ['amp', 'master', 'mix']);
});

test('the project header, the settings and nothing built all mean everything', () => {
  const before = base(), after = base();
  after.project = { tempo: 100 };
  assert.equal(staleBlocks(signatureOf(before), before, after), 'all');
  assert.equal(staleBlocks(null, before, base()), 'all');
});

test('no change, no stale blocks; moving a clip as a whole changes nothing in the block itself', () => {
  const before = base();
  before.blocks[0].parts = [{ at: 1, length: 2, name: '' }];
  const after = base();
  after.blocks[0].parts = [{ at: 5, length: 2, name: '' }];
  const stale = staleBlocks(signatureOf(before), before, after);
  assert.deepEqual([...stale].sort(), ['master', 'mix']);   // the mix places the clip, the block and the blocks before the mix keep their takes
  assert.equal(staleBlocks(signatureOf(base()), base(), base()).size, 0);
});

test('a new, removed or rewired block is picked up', () => {
  const before = base(), after = base();
  after.blocks.push(block('extra', 'midi'));
  after.blocks[4].froms.push({ block: 'extra', output: 'out', input: 'in2', disabled: false });
  assert.deepEqual([...staleBlocks(signatureOf(before), before, after)].sort(), ['extra', 'master', 'mix']);
});

test('a changed file makes only the blocks that read it, and what follows them, out of date', async () => {
  const { staleByFiles } = await import('../build-stale.mjs');
  const doc = base();
  const stale = staleByFiles({ drums: 'a', bass: 'x' }, { drums: 'b', bass: 'x' }, doc);
  assert.deepEqual([...stale].sort(), ['drums', 'kit', 'master', 'mix']);
  assert.equal(staleByFiles({ drums: 'a' }, { drums: 'a' }, doc).size, 0);
  assert.equal(staleByFiles(null, { drums: 'a' }, doc).size, 0);
  assert.deepEqual([...staleByFiles({}, { amp: 'a' }, doc)].sort(), ['amp', 'master', 'mix']);   // a block that starts to read a file
});

test('a new master seed only reaches the blocks whose seed follows it', () => {
  const before = base(), after = base();
  before.project = { tempo: 90, masterSeed: 1 };
  after.project = { tempo: 90, masterSeed: 2 };
  const stale = staleBlocks(signatureOf(before), before, after, ['bass']);
  assert.deepEqual([...stale].sort(), ['amp', 'bass', 'master', 'mix']);   // bass follows the master seed; the rest of the song keeps its build
  assert.equal(staleBlocks(signatureOf(before), before, after), 'all');     // not known yet: everything
  assert.equal(staleBlocks(signatureOf(before), before, after, []).size, 0);
});
