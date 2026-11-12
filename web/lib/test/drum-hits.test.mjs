import assert from 'node:assert/strict';
import test from 'node:test';
import { assembleDrums, drumHits, hitAudio, noiseStream, renderHit } from '../drum-core.mjs';
import { hitsInWindow } from '../drum-schedule.mjs';

const take = (pattern, bars = 2, song = '') => ({ params: { pattern, song, tempo: 120 }, sampleRate: 8000, firstBar: 1, bars, activeBars: Array.from({ length: bars }, (_, k) => 1 + k) });

test('the hits of a bar: kicks by pattern, snares on 4 and 12, hats on the even steps', () => {
  const a = drumHits(take('beat-A', 1)).hits, b = drumHits(take('beat-B', 1)).hits;
  assert.deepEqual(a.filter((h) => h.kind === 'kick').map((h) => h.step), [0, 8]);
  assert.deepEqual(b.filter((h) => h.kind === 'kick').map((h) => h.step), [0, 6, 8, 14]);
  assert.deepEqual(a.filter((h) => h.kind === 'snare').map((h) => h.step), [4, 12]);
  assert.equal(a.filter((h) => h.kind === 'hat').length, 8);
});

test('a hat or snare is the same sound wherever the pattern puts kicks (noise is addressed by position)', () => {
  const noise = noiseStream('77'), a = drumHits(take('beat-A')).hits, b = drumHits(take('beat-B')).hits;
  const noisy = (hits) => hits.filter((h) => !h.tonal);
  assert.deepEqual(noisy(a).map((h) => [h.start, h.noiseAt]), noisy(b).map((h) => [h.start, h.noiseAt]));
  const x = renderHit(noisy(a)[3], noise), y = renderHit(noisy(b)[3], noise);
  assert.deepEqual(x, y);
});

test('adding the hits equals the old whole-bar render, and hitAudio scales like the render', () => {
  const plan = drumHits(take('beat-B', 2, 'beat-A beat-B')), out = assembleDrums(plan, '5');
  assert.equal(out.length, plan.total);
  assert.ok(Math.max(...out) > 20000);
  assert.equal(hitAudio(Float32Array.of(-32768, 32767))[0], -1);
});

test('hits are triggered once per pass of the loop, in order, across a wrap and inside a region', () => {
  const hits = [{ at: 0 }, { at: 1 }, { at: 3 }];
  const whole = { start: 0, length: 4 }, at = (now) => (now - 10) % 4;   // the loop started at context time 10
  const first = hitsInWindow(hits, { span: whole, positionAt: at, from: 13.5, to: 15.2 });
  assert.deepEqual(first.map((e) => e.when), [14, 15]);
  assert.deepEqual(hitsInWindow(hits, { span: whole, positionAt: at, from: 13.9, to: 14.05 }).map((e) => e.when), [14]);
  assert.deepEqual(hitsInWindow(hits, { span: { start: 1, length: 2 }, positionAt: (now) => 1 + ((now - 10) % 2), from: 10, to: 14 }).map((e) => e.when), [10, 12]);
});

test('the hits add up to exactly what the server renders (conformance vectors of the built-in drums)', async () => {
  const { loadVectors } = await import('../dsp-conformance.mjs');
  const sources = loadVectors().sources || {};
  const names = Object.keys(sources).filter((name) => name.startsWith('drums:'));
  assert.ok(names.length >= 3);
  for (const name of names) {
    const vector = sources[name], bytes = Buffer.from(vector.channels[0], 'base64'), server = new Int16Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 2);
    const bars = Array.from({ length: vector.bars }, (_, k) => 1 + k);
    const mine = assembleDrums(drumHits({ params: vector.params, sampleRate: vector.sampleRate, firstBar: 1, bars: vector.bars, activeBars: bars }), vector.seed);
    assert.equal(mine.length, server.length, name);
    let differing = 0;
    for (let i = 0; i < mine.length; i++) if (mine[i] !== server[i]) differing++;
    assert.equal(differing, 0, `${name}: samples that differ from the server`);
  }
});
