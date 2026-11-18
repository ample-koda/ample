// Remix: saving and building. Every write is a promise that the test holds, so the order of events is fixed.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createBuildGate, createSaver } from '../../remix/lite-save.mjs';

const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const flush = async () => { for (let i = 0; i < 5; i++) await new Promise((resolve) => setImmediate(resolve)); };

function rig() {
  const writes = [], saved = [], states = [];
  const holds = [];
  const saver = createSaver({
    write: (projectId, path, text) => { const d = deferred(); writes.push({ projectId, path, text }); holds.push(d); return d.promise; },
    onSaved: (entry) => saved.push(`${entry.path}=${entry.text}`),
    onState: (state) => states.push(state),
  });
  return { saver, writes, saved, states, holds };
}

test('C1: a flush waits for a save that is already on its way', async () => {
  const r = rig();
  r.saver.queue('p', 'a.amplet', 'v1');
  const autosave = r.saver.flush();   // the autosave after 350 ms: the write starts
  await flush();
  assert.equal(r.writes.length, 1);
  let buildSaw = null;
  const forBuild = r.saver.flush().then((result) => { buildSaw = result; });   // Build asks while the write runs
  await flush();
  assert.equal(buildSaw, null, 'it does not come back before the write is done');
  r.holds[0].resolve();
  await autosave; await forBuild;
  assert.deepEqual(buildSaw, { ok: true, failed: [] });
  assert.deepEqual(r.saved, ['a.amplet=v1']);
});

test('C1: a newer text that comes in during a save is saved too, and the older write never marks it clean', async () => {
  const r = rig();
  r.saver.queue('p', 'a.amplet', 'v1');
  const first = r.saver.flush();
  await flush();
  r.saver.queue('p', 'a.amplet', 'v2');   // typed while v1 is written
  const second = r.saver.flush();
  r.holds[0].resolve();
  await flush();
  assert.equal(r.writes.length, 2, 'v2 is written after v1, never at the same time');
  assert.equal(r.writes[1].text, 'v2');
  assert.deepEqual(r.saved, [], 'v1 finished but v2 is newer: nothing is clean yet');
  assert.equal(r.saver.get('p', 'a.amplet'), 'v2', 'the newest text is what a build takes');
  r.holds[1].resolve();
  await first; await second;
  assert.deepEqual(r.saved, ['a.amplet=v2']);
  assert.equal(r.saver.size(), 0);
});

test('C1: every write goes to the project and the file it was typed for, also when another file or project is open by then', async () => {
  const r = rig();
  r.saver.queue('p1', 'a.amplet', 'text of a');
  r.saver.queue('p2', 'b.amplet', 'text of b');
  const done = r.saver.flush();
  await flush(); r.holds[0].resolve(); await flush(); r.holds[1].resolve();
  await done;
  assert.deepEqual(r.writes.map((w) => `${w.projectId}/${w.path}=${w.text}`), ['p1/a.amplet=text of a', 'p2/b.amplet=text of b']);
});

test('C2: a failed save stays, can be built, is tried again by the next flush (once), and the flush says it failed', async () => {
  const r = rig();
  r.saver.queue('p', 'a.amplet', 'v1');
  const first = r.saver.flush();
  await flush();
  r.holds[0].reject(new Error('quota'));
  const result = await first;
  assert.equal(result.ok, false);
  assert.equal(result.failed[0].path, 'a.amplet');
  assert.equal(r.saver.get('p', 'a.amplet'), 'v1', 'the text can still be got back');
  const files = [{ path: 'a.amplet', text: 'old' }, { path: 'b.amplet', text: 'b' }, { path: 'kick.wav', blob: 1 }];
  assert.deepEqual(r.saver.withDirty('p', files).map((f) => f.text ?? f.blob), ['v1', 'b', 1], 'a build takes the text of the editor, not the old one');
  assert.deepEqual(r.saver.withDirty('other', [{ path: 'a.amplet', text: 'old' }]).map((f) => f.text), ['old'], 'another project is left alone');
  assert.equal(r.writes.length, 1, 'no retry by itself');
  const again = r.saver.flush();
  await flush();
  assert.equal(r.writes.length, 2, 'the next flush tries it once');
  r.holds[1].resolve();
  assert.deepEqual(await again, { ok: true, failed: [] });
  assert.deepEqual(r.saved, ['a.amplet=v1']);
});

test('C2: a newer text wins over an older one that failed, and a retry never overwrites it', async () => {
  const r = rig();
  r.saver.queue('p', 'a.amplet', 'v1');
  const first = r.saver.flush();
  await flush();
  r.holds[0].reject(new Error('quota'));
  await first;
  r.saver.queue('p', 'a.amplet', 'v2');   // typed after the failure
  const retry = r.saver.flush();
  await flush();
  assert.deepEqual(r.writes.map((w) => w.text), ['v1', 'v2'], 'only the newest text is tried again');
  r.holds[1].resolve();
  assert.equal((await retry).ok, true);
  assert.deepEqual(r.saved, ['a.amplet=v2']);
});

test('C2: a write that keeps failing is tried once per flush, not in a loop', async () => {
  const r = rig();
  r.saver.queue('p', 'a.amplet', 'v1');
  const run = r.saver.flush();
  for (let i = 0; i < 5; i++) { await flush(); r.holds[i]?.reject(new Error('quota')); }
  assert.equal((await run).ok, false);
  assert.equal(r.writes.length, 1);
});

test('D: deleting a file takes its text out of the saver', async () => {
  const r = rig();
  r.saver.queue('p', 'a.amplet', 'v1');
  r.saver.drop('p', 'a.amplet');
  assert.equal(r.saver.size(), 0);
  r.saver.queue('p', 'b.amplet', 'v');
  r.saver.clear();
  assert.equal(r.saver.size(), 0);
  assert.deepEqual(await r.saver.flush(), { ok: true, failed: [] });
  assert.equal(r.writes.length, 0);
});

test('C3: two quick calls give one active build; a ticket frees only its own build; Stop of Live Preview aborts only its own build', () => {
  const gate = createBuildGate();
  const first = gate.reserve('normal'), second = gate.reserve('listen');   // in the same tick, before any await
  assert.ok(first);
  assert.equal(second, null, 'the second call does not get a build');
  assert.equal(gate.abort('listen'), false, 'the build of the person is not a build of Live Preview');
  assert.equal(first.controller.signal.aborted, false);
  assert.equal(gate.release(first), true);
  const third = gate.reserve('listen');
  assert.equal(gate.release(first), false, 'the old ticket does not free the new build');
  assert.equal(gate.busy(), true);
  assert.equal(gate.abort('listen'), true);
  assert.equal(third.controller.signal.aborted, true);
  assert.notEqual(third.controller, first.controller, 'every build has its own controller');
  gate.release(third);
  assert.equal(gate.busy(), false);
});

test('a flush with nothing to save does not report a state (the page must not show "saved" for text that is not queued yet)', async () => {
  const r = rig();
  assert.deepEqual(await r.saver.flush(), { ok: true, failed: [] });
  assert.equal(r.states.length, 0);
  r.saver.queue('p', 'a.amplet', 'v');
  const run = r.saver.flush();
  await flush(); r.holds[0].resolve(); await run;
  assert.equal(r.states.at(-1).dirty, 0);
});
