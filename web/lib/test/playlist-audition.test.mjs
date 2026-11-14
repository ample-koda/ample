// Playlist "Hear it" and the live solo scope (issues #283, #284 and the stop rule of the audition). Pure parts only: no audio, no browser.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createTakeCache } from '../take-cache.mjs';
import { createAuditionGuard } from '../audition-guard.mjs';
import { soloInPlan } from '../live-chain-model.mjs';

const buffer = (frames) => ({ length: frames, numberOfChannels: 2 });   // frames * 2 * 4 bytes

test('#283: a long run of new take ids stays within the byte budget, keyed by take id', async () => {
  const cache = createTakeCache({ budgetMb: 1 });   // 1 MiB: two buffers of 512 KiB
  const frames = 65536;
  for (let i = 0; i < 50; i++) {
    await cache.getDecoded(`take-${i}`, async () => buffer(frames));
    assert.ok(cache.bytes() <= 1024 * 1024, `over budget after ${i + 1} takes`);
  }
  assert.equal(cache.size(), 2);
  assert.ok(cache.has('take-49') && cache.has('take-48') && !cache.has('take-0'));
});

test('#283: the same take id from the studio and from the playlist decodes once', async () => {
  const cache = createTakeCache({ budgetMb: 8 });
  let loads = 0;
  const load = async () => { loads++; return buffer(1000); };
  const [a, b] = await Promise.all([cache.getDecoded('take-x', load), cache.getDecoded('take-x', load)]);
  assert.equal(loads, 1);
  assert.equal(a, b);
  assert.equal(cache.size(), 1);
});

test('#283: the playlist uses the live chain's key for a take (shared with the studio), not a second key space', () => {
  const source = readFileSync(new URL('../../app/playlist-preview.js', import.meta.url), 'utf8');
  assert.match(source, /takeCache\.getDecoded\(`chain:\$\{takeId\}`,/);
  assert.doesNotMatch(source, /playlist:\$\{/);
});

const bus = (id, solo) => ({ id, tpl: 'bus', params: solo ? { solo: 'on' } : {} });

test('#284: a solo bus inside the plan mutes the other plan buses', () => {
  const nodes = [bus('a', true), bus('b', false), bus('c', false)];
  assert.equal(soloInPlan(nodes, new Set(['a', 'b'])), true);
});

test('#284: a solo bus outside the plan (another branch, downstream, removed) does not silence a targeted audition', () => {
  const nodes = [bus('a', false), bus('b', false), bus('other', true), bus('downstream', true)];
  assert.equal(soloInPlan(nodes, new Set(['a', 'b'])), false);
  assert.equal(soloInPlan([bus('a', false)], new Set(['a', 'b'])), false);   // the soloed bus was removed from the song
});

test('#284: two independent branches judge their own solo only', () => {
  const nodes = [bus('x1', true), bus('x2', false), bus('y1', false), bus('y2', false)];
  assert.equal(soloInPlan(nodes, new Set(['x1', 'x2'])), true);
  assert.equal(soloInPlan(nodes, new Set(['y1', 'y2'])), false);
});

test('#284: only buses solo; a soloed non-bus block does not count', () => {
  assert.equal(soloInPlan([{ id: 'm', tpl: 'mixer', params: { solo: 'on' } }], new Set(['m'])), false);
});

test('audition guard: Stop, a newer audition and a changed song end an audition; an unrelated request does not', () => {
  let song = 'a.amplet';
  const guard = createAuditionGuard(() => song);
  const first = guard.begin();
  assert.equal(first(), true);   // a declined confirm raises nothing: it keeps playing
  guard.invalidate();            // Stop, Clear or the song really opened
  assert.equal(first(), false);
  const second = guard.begin();
  guard.begin();                 // a newer Hear it
  assert.equal(second(), false);
  const third = guard.begin();
  song = 'b.amplet';             // the song changed without a notification before the source started
  assert.equal(third(), false);
});

test('audition stops on the real song change, not on the open request', () => {
  const source = readFileSync(new URL('../../app/playlist-preview.js', import.meta.url), 'utf8');
  assert.match(source, /addEventListener\('ample:song-opened', \(\) => stopMix\(\)\)/);
  assert.doesNotMatch(source, /addEventListener\('ample:open'/);
  assert.match(source, /await resumed;/);   // a suspended context is resumed before the source starts
});
