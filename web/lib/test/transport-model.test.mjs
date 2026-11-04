import test from 'node:test';
import assert from 'node:assert/strict';
import { blockedReason, buildButton, buildSummary, failedBadge, masterTakeOf, playButton, playingLabel, runningText, songState, stopButton } from '../transport-model.mjs';

const nodes = (...names) => names.map((id) => ({ id, flags: [] }));

test('the master take is the master block, else the last block with a take', () => {
  assert.equal(masterTakeOf({ master: 'm', a: 'x' }, ['a', 'master']), 'm');
  assert.equal(masterTakeOf({ a: 'x', b: 'y' }, ['a', 'b', 'c']), 'y');
  assert.equal(masterTakeOf({}, ['a']), null);
});

test('the song state lists what needs a build, what failed and ignores blocks that are off', () => {
  const list = [...nodes('a', 'b', 'c', 'd'), { id: 'off', flags: ['disabled'] }, { id: 'proxy', proxy: true, flags: [] }];
  const s = songState(list, { a: 't1' }, { a: { status: 'cached' }, c: { status: 'failed', message: 'no file' }, d: { status: 'skipped' } });
  assert.deepEqual(s.needs, ['b']);
  assert.deepEqual(s.failed, [{ name: 'c', message: 'no file' }]);
  assert.deepEqual(s.skipped, ['d']);
  assert.equal(s.upToDate, false);
  assert.equal(songState(nodes('a'), { a: 't' }, {}).upToDate, true);
});

test('buttons say why they are disabled', () => {
  const ok = { hasDoc: true, peek: false, errors: 0, building: false };
  assert.equal(blockedReason(ok), '');
  assert.match(blockedReason({ ...ok, errors: 2 }), /Fix the 2 errors/);
  assert.match(blockedReason({ ...ok, peek: true }), /Read-only/);
  assert.match(blockedReason({ ...ok, building: true }), /build is running/);
  assert.equal(buildButton({ ...ok, errors: 1 }).disabled, true);
  assert.match(buildButton(ok).title, /Ctrl\+B/);
});

test('Play plays the built song and builds first when something is out of date', () => {
  const ok = { hasDoc: true, peek: false, errors: 0, building: false };
  const fresh = { needs: [], failed: [], skipped: [], upToDate: true };
  assert.deepEqual([playButton(ok, fresh, 'm').label, playButton(ok, fresh, 'm').action], ['▶ Play', 'play']);
  const stale = { needs: ['drums', 'bass'], failed: [], skipped: [], upToDate: false };
  const p = playButton(ok, stale, 'm');
  assert.equal(p.label, '▶ Build and play');
  assert.equal(p.action, 'build-play');
  assert.match(p.title, /Out of date: drums, bass/);
  assert.equal(playButton(ok, fresh, null).action, 'build-play');   // nothing to play yet
  assert.match(playButton(ok, { ...fresh, upToDate: false, failed: [{ name: 'x' }] }, 'm').title, /failed last time: x/);
  assert.equal(playButton({ ...ok, errors: 1 }, fresh, 'm').disabled, true);
});

test('one Stop button cancels the build first, then stops the audio', () => {
  assert.equal(stopButton({ building: true, playing: true }).mode, 'build');
  assert.match(stopButton({ building: true, playing: true }).title, /Press again/);
  assert.equal(stopButton({ building: false, playing: true }).mode, 'audio');
  assert.equal(stopButton({ building: false, playing: false }).disabled, true);
});

test('what plays is named: the song, a block, a project file', () => {
  const takes = { drums: 't-drums', master: 't-master' };
  assert.equal(playingLabel({ playing: false }, takes, 't-master'), '');
  assert.equal(playingLabel({ playing: true, take: 't-master' }, takes, 't-master'), '▶ the song');
  assert.equal(playingLabel({ playing: true, take: 't-drums' }, takes, 't-master'), '▶ drums');
  assert.equal(playingLabel({ playing: true, source: 'samples/kit/kick.wav' }, takes, null), '▶ kick.wav');
  assert.equal(playingLabel({ playing: true, take: 'other' }, takes, null), '▶ audio');
});

test('running blocks, the failed badge and the build summary are short and readable', () => {
  assert.equal(runningText({ a: { state: 'running' }, b: { state: 'queued' }, c: { state: 'running' }, d: { state: 'running' } }), ' · a, c and 1 more'.replace('a, c and 1 more', 'a, c and 1 more'));
  assert.equal(runningText({}), '');
  assert.equal(failedBadge({ failed: [], skipped: [] }), null);
  const badge = failedBadge({ failed: [{ name: 'drums', message: 'no sample' }], skipped: ['mix'] });
  assert.equal(badge.text, '1 failed, 1 waiting');
  assert.match(badge.title, /drums: no sample/);
  assert.equal(buildSummary({ blocks: [{ block: 'a', status: 'failed' }, { Block: 'b', Status: 'failed' }] }, false, 0), 'Build finished with 2 failed blocks: a, b.');
  assert.equal(buildSummary({ blocks: [] }, false, 0), '');
  assert.equal(buildSummary(null, true, 3), 'Build stopped: 3 blocks were ready and are kept.');
});

test('a deferred block (it only forwards its recipe into an effect chain) never has a take and does not keep the song out of date', () => {
  const s = songState(nodes('tone', 'seat'), { seat: 't' }, { tone: { status: 'deferred' }, seat: { status: 'cached' } });
  assert.deepEqual(s.needs, []);
  assert.equal(s.upToDate, true);
  assert.equal(playButton({ hasDoc: true, errors: 0 }, s, 't').label, '▶ Play');
});

test('a single running block says which step it is on and how far it is', () => {
  assert.equal(runningText({ score: { state: 'running', steps: [{ name: 'cello (3/5)' }], fraction: 0.4 } }), ' · score · cello (3/5) 40%');
  assert.equal(runningText({ score: { state: 'running', steps: [], fraction: 0 } }), ' · score');
  assert.equal(runningText({ a: { state: 'running', steps: [{ name: 'x' }], fraction: 0.5 }, b: { state: 'running' } }), ' · a, b');
});
