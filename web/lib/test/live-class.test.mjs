import test from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { LIVE_CLASS, liveClassOf, whyNotLive, hasLocalPreview } from '../live-class.mjs';

const dir = new URL('../../../spec/templates/', import.meta.url);
const templates = readdirSync(dir).filter((f) => f.endsWith('.json')).map((f) => ({ id: f.slice(0, -5), json: JSON.parse(readFileSync(new URL(f, dir), 'utf8')) }));

test('every template has a live class and no stale entries remain', () => {
  const ids = templates.map((t) => t.id);
  assert.deepEqual(ids.filter((id) => !LIVE_CLASS[id]), []);
  assert.deepEqual(Object.keys(LIVE_CLASS).filter((id) => !ids.includes(id)), []);
});

test('the class agrees with the livePreview flag of the template', () => {
  for (const { id, json } of templates) {
    if (json.livePreview === true) assert.equal(liveClassOf(id), 'live', id);
    else if (id !== 'delay-llama') assert.notEqual(liveClassOf(id), 'live', id);
  }
});

test('every block that is not live has a reason', () => {
  for (const { id } of templates) if (liveClassOf(id) !== 'live') assert.ok(whyNotLive(id).length > 10, id);
  assert.equal(whyNotLive('delay'), '');
});

test('visual and data blocks have a reason that can follow "No audio of its own:"', async () => {
  const { liveClassOf, whyNotLive } = await import('../live-class.mjs');
  for (const id of ['scorer', 'variants', 'mood-scene', 'lyrics', 'curve', 'follow']) {
    assert.ok(['visual', 'data'].includes(liveClassOf(id)), id);
    assert.ok(whyNotLive(id).length > 10, id);
  }
});

test('the Advanced window previews the same blocks as before the hard-coded set was retired', () => {
  const before = ['eq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'volume', 'convolution', 'denoise', 'chorus', 'tremolo', 'pan', 'width', 'phaser', 'flanger', 'gate', 'limiter', 'deesser', 'master'];
  assert.deepEqual(Object.keys(LIVE_CLASS).filter(hasLocalPreview).sort(), [...before].sort());
});

test('every template flagged livePreview has a live class and a window preview (or a documented own path)', () => {
  for (const { id, json } of templates) if (json.livePreview === true) assert.ok(hasLocalPreview(id) || ['peq', 'delay-llama'].includes(id), id);
});
