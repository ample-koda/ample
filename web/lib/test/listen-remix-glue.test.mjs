// The glue of Live Preview in Remix (web/remix/lite-listen.js) with a fake page and a fake audio context: what Listen is bound to when the song that is built,
// the file that is open, the selection of bars and the project can all change while a build runs. One test for every finding of the review of PR 779.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createRemixListen } from '../../remix/lite-listen.js';
import { createListenBuilder } from '../listen-cancel.mjs';
import { createListenSession } from '../listen-session.mjs';

const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const flush = async () => { for (let i = 0; i < 6; i++) await new Promise((resolve) => setImmediate(resolve)); };
const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const SONG = 'composition "a" { ample 1  tempo 120  master-seed 1 }\nmidi-clip theme at 1 for 4 {\n  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100\n}\n';
const SONG34 = 'composition "w" { ample 1  tempo 60  meter 3/4  master-seed 1 }\nmidi-clip theme at 1 for 4 {\n  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100\n}\n';
const COMPONENT = 'component koda.echo version 1 {\n}\n';

class FakeContext {
  constructor() { this.currentTime = 0; this.state = 'running'; this.destination = {}; }
  async resume() {}
  async decodeAudioData(data) { return { duration: data.duration }; }
  createGain() { return { gain: { setValueAtTime() {}, linearRampToValueAtTime() {} }, connect: (to) => to }; }
  createBufferSource() { return { buffer: null, connect: (to) => to, start() {}, stop() {} }; }
}

/** What a build of the page gives: the song it built, its text as the build took it, and its sound (a fake blob that is `seconds` long). */
const outcome = (entry, entryText, seconds, id) => ({ ok: true, entry, entryText, outputId: id, blob: { arrayBuffer: async () => ({ duration: seconds }) } });

function rig({ active = 'song.amplet', entryPath = 'song.amplet', pause = 5, gap = 0 } = {}) {
  const els = new Map();
  const $ = (selector) => {
    if (!els.has(selector)) els.set(selector, { hidden: false, dataset: {}, textContent: '', value: '', title: '', classList: { toggle() {} }, setAttribute() {} });
    return els.get(selector);
  };
  const page = { building: false, active, text: SONG, aborts: 0, marks: [], toasts: [], builds: [] };
  const host = {
    $,
    build: (options) => { const d = deferred(); page.builds.push({ d, options }); page.building = true; return d.promise.finally(() => { page.building = false; }); },
    getText: () => page.text,
    activePath: () => page.active,
    entryPath: () => entryPath,
    isSong: (path) => path.endsWith('.amplet'),
    hasSong: () => true,
    setMarks: (marks) => { page.marks = marks; },
    buildError: () => null,
    isBuilding: () => page.building,
    abortBuild: () => { page.aborts++; },
    pauseOutput() {},
    toast: (text) => page.toasts.push(text),
  };
  globalThis.window = { AudioContext: FakeContext };
  const listen = createRemixListen(host, { pause, gap });
  return { listen, page, context: () => listen.player().entry && null };
}
const finish = (r) => r.listen.stop();

test('finding 1: the tempo, the meter and the marks come from the song as it was built, not from the file that is open', async () => {
  const r = rig({ active: 'components/echo.amplec' });
  r.page.text = COMPONENT;
  const started = r.listen.start();
  await flush();
  r.page.builds[0].d.resolve(outcome('song.amplet', SONG34, 12, 'o1'));
  await started; await flush();
  const entry = r.listen.player().entry;
  assert.equal(entry.path, 'song.amplet');
  assert.equal(entry.quarters, 3, 'the meter of the built song, 3/4');
  assert.equal(entry.barSeconds, 3, '3 beats at 60 BPM');
  r.listen.refreshMarks();
  assert.deepEqual(r.page.marks, [], 'the component that is open gets no marks of the song');
  r.page.active = 'song.amplet'; r.page.text = SONG34;
  r.listen.refreshMarks();
  assert.ok(r.page.marks.length > 0, 'the song that was built gets them');
  r.page.active = 'components/echo.amplec'; r.page.text = COMPONENT;
  r.listen.refreshMarks();
  assert.deepEqual(r.page.marks, [], 'and loses them again when the component opens');
  r.page.active = 'other.amplet'; r.page.text = SONG34;   // another file with the same lines in it
  r.listen.refreshMarks();
  assert.deepEqual(r.page.marks, [], 'marks are drawn only in the file that was built, whatever the text of another file looks like');
  finish(r);
});

test('finding 2: a range chosen while the first build runs is the range of the sound when it is ready', async () => {
  const r = rig();
  const started = r.listen.start();
  await flush();
  await r.listen.select({ target: 'bars', first: 2, last: 3 });   // the build is still running
  r.page.builds[0].d.resolve(outcome('song.amplet', SONG, 16, 'o1'));
  await started; await flush();
  const entry = r.listen.player().entry;
  assert.equal(entry.job.kind, 'bars');
  assert.deepEqual(entry.segments, [{ from: 2, to: 6 }], 'bars 2-3 of a 2 second bar');
  finish(r);
});

test('finding 2: a preview that waits for the end of the loop takes the new selection at once', async () => {
  const r = rig();
  const started = r.listen.start();
  await flush();
  r.page.builds[0].d.resolve(outcome('song.amplet', SONG, 16, 'o1'));
  await started; await flush();
  r.listen.changed();   // a change: the next build (after the short pause)
  await wait(40); await flush();
  assert.equal(r.page.builds.length, 2);
  r.page.builds[1].d.resolve(outcome('song.amplet', SONG, 16, 'o2'));
  await flush(); await wait(20);
  assert.equal(r.listen.player().pending, true, 'the new sound waits for the loop boundary');
  await r.listen.select({ target: 'bars', first: 2, last: 3 });
  const now = r.listen.player();
  assert.equal(now.pending, false);
  assert.equal(now.entry.take, 'o2', 'the newest sound, cut for the new selection');
  assert.deepEqual(now.entry.segments, [{ from: 2, to: 6 }]);
  finish(r);
});

test('findings 2 and 3: a selection that does not fit the new sound keeps the last good one; a selection that fits it later brings the status back', async () => {
  const r = rig();
  await r.listen.select({ target: 'bars', first: 5, last: 6 });
  const started = r.listen.start();
  await flush();
  r.page.builds[0].d.resolve(outcome('song.amplet', SONG, 16, 'o1'));   // 8 bars: bars 5-6 fit
  await started; await flush();
  assert.equal(r.listen.player().entry.take, 'o1');
  r.listen.changed();
  await wait(40); await flush();
  r.page.builds[1].d.resolve(outcome('song.amplet', SONG, 6, 'o2'));   // the song got shorter: 3 bars, bars 5-6 do not fit
  await flush(); await wait(20);
  assert.equal(r.listen.state().status, 'error');
  assert.match(r.listen.state().error, /no bars 5-6/);
  assert.equal(r.listen.player().entry.take, 'o1', 'the last good build keeps playing');
  r.page.toasts.length = 0;
  await r.listen.select({ first: 5, last: 6 });   // fits the sound that plays, not the newest one
  assert.equal(r.listen.player().entry.take, 'o1');
  assert.equal(r.page.toasts.length, 0, 'the sound that plays is the last usable render: it is not lost when the newest one does not fit');
  await r.listen.select({ first: 1, last: 2 });   // the newest sound fits this selection
  assert.equal(r.listen.player().entry.take, 'o2');
  assert.equal(r.listen.state().status, 'playing');
  assert.equal(r.listen.state().error, null);
  assert.equal(r.listen.state().playing, 2, 'the status says which build is heard');
  r.page.toasts.length = 0;
  await r.listen.select({ first: 50, last: 60 });   // fits no sound
  assert.equal(r.page.toasts.length, 1, 'it is said');
  assert.equal(r.listen.player().entry.take, 'o2', 'and what plays does not change');
  finish(r);
});

test('finding 4: another song ends the listening, a component does not, and a late sound of the old build plays nothing', async () => {
  const r = rig();
  const started = r.listen.start();
  await flush();
  r.listen.fileOpened('components/echo.amplec');
  assert.equal(r.listen.active(), true, 'a component of the project is open: still listening');
  r.listen.fileOpened('song.amplet');
  assert.equal(r.listen.active(), true, 'the same song again');
  r.listen.fileOpened('other.amplet');
  assert.equal(r.listen.active(), false, 'another song');
  assert.equal(r.listen.state().status, 'stopped');
  assert.match(r.page.toasts.join(' '), /another song/);
  assert.deepEqual(r.page.marks, []);
  r.page.builds[0].d.resolve(outcome('song.amplet', SONG, 12, 'o1'));   // the build of the old session ends late
  await started; await flush();
  assert.equal(r.listen.player().playing, false, 'nothing is heard');
  assert.equal(r.listen.state().status, 'stopped');
});

test('finding 4: Stop, then Listen on another song: a late sound of the first build does not change the new session', async () => {
  const r = rig();
  const first = r.listen.start();
  await flush();
  r.listen.stop();
  const second = r.listen.start();
  await flush();
  assert.equal(r.page.builds.length, 1, 'the second build waits for the build of the page that is running');
  r.page.builds[0].d.resolve(outcome('song.amplet', SONG, 12, 'old'));
  await first; await flush();
  assert.equal(r.listen.player().playing, false, 'the old sound is not played');
  await wait(400); await flush();   // the second build polls the page every 120 ms
  r.page.builds[1]?.d.resolve(outcome('song.amplet', SONG, 12, 'new'));
  await second; await flush();
  assert.equal(r.listen.player().entry?.take, 'new');
  finish(r);
});

test('finding 5: the distance between two builds also counts from Stop to Listen', async () => {
  const r = rig({ gap: 60000 });
  const first = r.listen.start();
  await flush();
  r.page.builds[0].d.resolve(outcome('song.amplet', SONG, 12, 'o1'));
  await first; await flush();
  r.listen.stop();
  const again = r.listen.start();
  await wait(350);
  assert.equal(r.page.builds.length, 1, 'no second build within the distance');
  assert.match(r.listen.state().status === 'building' ? 'building' : 'waiting', /building|waiting/);
  r.listen.stop();
  await again;
});

test('finding 5: Stop during the wait for the distance cancels no build of the page, also not one that started meanwhile', async () => {
  const r = rig({ gap: 60000 });
  const first = r.listen.start();
  await flush();
  r.page.builds[0].d.resolve(outcome('song.amplet', SONG, 12, 'o1'));
  await first; await flush();
  r.listen.stop();
  const again = r.listen.start();
  await wait(300);
  r.page.building = true;   // the person starts a build of the page while Listen waits for the distance
  r.listen.stop();
  await wait(300);
  assert.equal(r.page.aborts, 0, 'Stop did not touch that build');
  await again;
});

test('the builder: the pause before a build is not a build of ours, and a build that started during it is waited for too', async () => {
  const stopped = [], built = [];
  const store = { busy: false }, pauses = [];
  const builder = createListenBuilder({
    isBusy: () => store.busy,
    stopStore: () => stopped.push('stop'),
    beforeBuild: () => { const d = deferred(); pauses.push(d); return d.promise; },
    buildNow: async (job) => { built.push(job); return job; },
    sleep: () => wait(1),
    delay: 1,
  });
  const signal = { cancelled: false };
  const building = builder.build({ n: 1 }, signal);
  await flush();
  builder.cancel();
  assert.deepEqual(stopped, [], 'a Stop during the pause cancels nothing');
  store.busy = true;   // a build of the page starts during the pause
  pauses[0].resolve(true);
  await wait(20);
  assert.deepEqual(built, [], 'it waits for that build');
  store.busy = false;
  await wait(20);
  assert.equal(pauses.length, 2, 'and then the pause is made again');
  pauses[1].resolve(true);
  assert.deepEqual(await building, { n: 1 });
  signal.cancelled = true;
  const cancelled = builder.build({ n: 2 }, signal);
  await flush();
  assert.equal(await cancelled, null);
});

test('the session: recover brings the status back to playing after a failed play, and only while the session is on', async () => {
  const states = [];
  let fail = true;
  const session = createListenSession({
    build: async () => ({}), cancelBuild() {}, stopPlayback() {}, onChange: (s) => states.push(s.status),
    play: async () => { if (fail) throw new Error('bars beyond the end'); },
  });
  await session.start({});
  assert.equal(session.state().status, 'error');
  session.recover(7);
  assert.equal(session.state().status, 'playing');
  assert.equal(session.state().error, null);
  assert.equal(session.state().playing, 7);
  session.stop();
  session.recover(9);
  assert.equal(session.state().status, 'stopped', 'a stopped session stays stopped');
  fail = false;
});
