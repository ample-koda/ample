import test from 'node:test';
import assert from 'node:assert/strict';
import { createSession, createPerformMixer, clampValue, quote, parts } from '../performance-mixer-model.mjs';

const song = () => ({ nodes: [{ id: 'mix1', params: { gains: '"0 -3 0 0"', pan: '"c c c c"' } }, { id: 'rev', params: { mix: '20 %' } }, { id: 'bus1', params: { gains: '"0 0 0 0"', solo: 'off' } }] });
const rig = (over = {}) => {
  const doc = song(); const log = { preview: [], edits: [], reverts: [] };
  const m = createPerformMixer({
    nodes: () => doc.nodes, preview: (b, n, v) => log.preview.push([b, n, v]), revert: (b, n) => log.reverts.push([b, n]),
    edit: over.edit || (async (ops) => { log.edits.push(ops); return true; }),
  });
  return { doc, log, m, s: m.session };
};
const before = (r) => JSON.stringify(r.doc);

test('a session move is read back and heard; the song is not edited', () => {
  const r = rig(); const snap = before(r);
  assert.equal(r.m.performSet('mix1', 'gains', '"-6 -3 0 0"'), true);
  assert.equal(r.s.read('mix1', 'gains', '"0 -3 0 0"'), '"-6 -3 0 0"');
  assert.deepEqual(r.log.preview.at(-1), ['mix1', 'gains', '"-6 -3 0 0"']);
  assert.equal(r.log.edits.length, 0); assert.equal(before(r), snap);
});

test('values are clamped to the parameter ranges and bad ones are refused', () => {
  const r = rig();
  r.m.performSet('mix1', 'gains', '"99 -999 abc 3"');
  assert.equal(r.s.read('mix1', 'gains', '"0 0 0 0"'), '"12 -60 0 3"');
  assert.equal(r.m.performSet('mix1', 'pan', '"x"'), false);
  assert.equal(r.s.setInput('mix1', 'gains', 4.5, '1'), null);
  assert.equal(r.m.performSet('rev', 'mix', '250 %', { min: 0, max: 100 }), true);
  assert.equal(r.s.read('rev', 'mix', '20 %'), '100 %');
  assert.equal(clampValue('-5 dB', { min: -3, max: 6 }), '-3 dB');
  assert.equal(clampValue('hello', { min: 0, max: 1 }), 'hello');
  assert.equal(clampValue('  ', { min: 0 }), null); assert.equal(clampValue(undefined), null);
  assert.equal(r.m.performSet('rev', 'mix', NaN === 1 ? 1 : undefined), false);
  assert.deepEqual(parts(undefined, '0 0 0 0'), ['0', '0', '0', '0']);
});

test('mute and solo are flags: unmute and un-solo give the fader values back, mute wins over solo', () => {
  const r = rig(); const saved = '"0 -3 0 0"';
  r.m.performMute('mix1', 0);
  assert.deepEqual(r.s.effectiveGains('mix1', saved), ['-60', '-3', '0', '0']);
  assert.deepEqual(r.log.preview.at(-1), ['mix1', 'gains', '"-60 -3 0 0"']);
  r.m.performMute('mix1', 0);
  assert.deepEqual(r.s.effectiveGains('mix1', saved), ['0', '-3', '0', '0']);
  r.m.performSolo('mix1', 1);
  assert.deepEqual(r.s.effectiveGains('mix1', saved), ['-60', '-3', '-60', '-60']);
  r.m.performSolo('mix1', 2);
  assert.deepEqual(r.s.effectiveGains('mix1', saved), ['-60', '-3', '0', '-60']);
  r.m.performMute('mix1', 1); // muted and soloed: mute wins
  assert.deepEqual(r.s.effectiveGains('mix1', saved), ['-60', '-60', '0', '-60']);
  r.m.performSolo('mix1', 1, false); r.m.performSolo('mix1', 2, false); r.m.performMute('mix1', 1, false);
  assert.deepEqual(r.s.effectiveGains('mix1', saved), ['0', '-3', '0', '0']);
  assert.equal(r.s.size, 0);
  assert.equal(r.m.performMute('mix1', 99), false);
});

test('mute and solo work the same on a bus input, a fader move under a mute is kept', () => {
  const r = rig(); const saved = '"0 0 0 0"';
  r.m.performSolo('bus1', 3);
  assert.deepEqual(r.s.effectiveGains('bus1', saved), ['-60', '-60', '-60', '0']);
  r.m.performSet('bus1', 'gains', '"0 0 0 -8"');
  assert.deepEqual(r.s.effectiveGains('bus1', saved), ['-60', '-60', '-60', '-8']);
  r.m.performMute('bus1', 3); r.m.performSet('bus1', 'gains', '"0 0 0 -5"'); r.m.performMute('bus1', 3);
  assert.equal(r.s.effectiveGains('bus1', saved)[3], '-5');
});

test('the bus solo PARAMETER is not confused with a per-input solo flag', () => {
  const r = rig();
  r.m.performSet('bus1', 'solo', 'on');
  assert.deepEqual(r.s.effectiveGains('bus1', '"0 0 0 0"'), ['0', '0', '0', '0']);
  assert.equal(r.s.isSoloed('bus1', 0), false);
  assert.deepEqual(r.s.compose(r.doc.nodes).edits, [{ type: 'setParameter', block: 'bus1', name: 'solo', value: 'on' }]);
});

test('reset restores exactly the song values in the sound, including defaults the song does not write', () => {
  const r = rig();
  r.m.performSet('mix1', 'gains', '"-6 -3 0 0"'); r.m.performMute('mix1', 2); r.m.performSet('mix1', 'pan', '"l c c c"'); r.m.performSet('rev', 'mix', '90 %');
  r.log.preview.length = 0;
  r.m.performReset();
  assert.equal(r.s.size, 0);
  assert.deepEqual(r.log.reverts.sort(), [['mix1', 'gains'], ['mix1', 'pan'], ['rev', 'mix']].sort());
  assert.equal(r.s.read('mix1', 'gains', '"0 -3 0 0"'), '"0 -3 0 0"');
  assert.deepEqual(r.s.effectiveGains('mix1', '"0 -3 0 0"'), ['0', '-3', '0', '0']);
  assert.equal(r.log.edits.length, 0);
  // without a revert hook the saved value is pushed instead
  const preview = [];
  const m = createPerformMixer({ nodes: () => song().nodes, preview: (...a) => preview.push(a), edit: async () => true });
  m.performSet('rev', 'mix', '90 %'); preview.length = 0; m.performReset();
  assert.deepEqual(preview, [['rev', 'mix', '20 %']]);
});

test('Apply is ONE edit call, writes what is heard against the song as it is now, and clears only after success', async () => {
  const r = rig();
  r.m.performSet('mix1', 'gains', '"-6 -3 0 0"'); r.m.performMute('mix1', 3); r.m.performSet('mix1', 'pan', '"l c c c"'); r.m.performSet('rev', 'mix', '40 %');
  r.doc.nodes[0].params.gains = '"0 -3 -9 0"'; // the song changed meanwhile: input 3 (index 2) is not overwritten by a stale value
  const res = await r.m.performApply();
  assert.equal(r.log.edits.length, 1);
  assert.deepEqual(r.log.edits[0], [
    { type: 'setParameter', block: 'mix1', name: 'gains', value: '"-6 -3 -9 -60"' },
    { type: 'setParameter', block: 'mix1', name: 'pan', value: '"l c c c"' },
    { type: 'setParameter', block: 'rev', name: 'mix', value: '40 %' },
  ]);
  assert.deepEqual([res.ok, res.applied], [true, 3]);
  assert.equal(r.s.size, 0);
});

test('a failed Apply (false or a throw) keeps every override and can be applied again', async () => {
  for (const bad of [async () => false, async () => { throw new Error('boom'); }]) {
    let calls = 0; const ok = async () => { calls++; return true; };
    const r = rig({ edit: async (ops) => { r.log.edits.push(ops); return calls++ === 0 ? bad() : true; } });
    r.m.performSet('mix1', 'gains', '"-6 -3 0 0"'); r.m.performMute('mix1', 1);
    const first = await r.m.performApply();
    assert.equal(first.ok, false); assert.equal(first.applied, 0);
    assert.equal(r.s.size, 2);
    assert.deepEqual(r.s.effectiveGains('mix1', '"0 -3 0 0"'), ['-6', '-60', '0', '0']);
    const second = await r.m.performApply();
    assert.equal(second.ok, true); assert.equal(r.s.size, 0);
    assert.deepEqual(r.log.edits[0], r.log.edits[1]);
    void ok;
  }
});

test('a move made while Apply runs is not lost; overlapping Apply calls are refused', async () => {
  let release; const gate = new Promise((res) => { release = res; });
  const r = rig({ edit: async () => { await gate; return true; } });
  r.m.performSet('rev', 'mix', '40 %'); r.m.performSet('mix1', 'gains', '"-6 -3 0 0"');
  const p = r.m.performApply();
  assert.equal((await r.m.performApply()).busy, true);
  r.m.performSet('rev', 'mix', '55 %'); // moved after the snapshot
  release(); await p;
  assert.equal(r.s.read('rev', 'mix', 'x'), '55 %');
  assert.equal(r.s.has('mix1', 'gains'), false);
});

test('renamed or deleted blocks: skipped and dropped, never written under another name', async () => {
  const r = rig();
  r.m.performSet('old-name', 'mix', '40 %'); r.m.performSet('rev', 'mix', '40 %');
  const res = await r.m.performApply();
  assert.deepEqual(res.skipped, ['old-name']);
  assert.deepEqual(r.log.edits[0].map((e) => e.block), ['rev']);
  assert.equal(r.s.size, 0);
  r.m.performSet('rev', 'mix', '70 %'); r.m.performMute('mix1', 0);
  r.doc.nodes.splice(0, 2); // both gone
  r.log.preview.length = 0; r.m.resync();
  assert.equal(r.s.size, 0); assert.equal(r.log.preview.length, 0);
});

test('unchanged values make no edit; with nothing to write Apply does not call edit', async () => {
  const r = rig();
  r.m.performSet('rev', 'mix', '20 %'); r.m.performSet('mix1', 'gains', '"0 -3 0 0"');
  const res = await r.m.performApply();
  assert.deepEqual([res.ok, res.applied, r.log.edits.length, r.s.size], [true, 0, 0, 0]);
});

test('resync pushes the heard values again (twice, the first meets the chain dropping a preview of a changed song)', () => {
  const r = rig();
  r.m.performSet('rev', 'mix', '40 %'); r.m.performMute('mix1', 0);
  r.log.preview.length = 0; r.m.resync();
  assert.equal(r.log.preview.length, 4);
  assert.deepEqual(r.log.preview.slice(0, 2).sort(), r.log.preview.slice(2).sort());
  assert.ok(r.log.preview.some((p) => p[0] === 'mix1' && p[2] === '"-60 -3 0 0"'));
});

test('quote and the session notify subscribers', () => {
  assert.equal(quote(['0', '-60']), '"0 -60"');
  const s = createSession(); let n = 0; const off = s.subscribe(() => n++);
  s.set('a', 'x', '1'); s.setMute('a', 0, true); s.reset(); off(); s.set('a', 'x', '2');
  assert.equal(n, 3);
});
