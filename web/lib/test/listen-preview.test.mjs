// Live Preview in the code editor (plan/live-preview/PLAN.md): the session, the loop arithmetic and the marks
import test from 'node:test';
import assert from 'node:assert/strict';
import { createListenSession } from '../listen-session.mjs';
import { barsLoop, heardLength, playSegments, previewTimeAt, rangeBuild, silentWindows, takeTimeAt, wavSlice } from '../listen-segments.mjs';
import { autoMarks, blockSpan, chordMarks, drumMarks, melodyMarks, moveMarks, notesLines, playbackMarks, soundingTokens } from '../listen-highlight.mjs';

// ---- the session ----

function rig({ fail = () => false } = {}) {
  const log = [], timers = new Map();
  let nextTimer = 1, running = 0, most = 0;
  const builds = [];
  const deps = {
    build: (job, signal) => new Promise((resolve, reject) => {
      running++; most = Math.max(most, running);
      builds.push({ job, resolve: (v) => { running--; resolve(v); }, reject: (e) => { running--; reject(e); }, signal });
      log.push(`build ${job.text}`);
    }),
    cancelBuild: () => { log.push('cancel'); const b = builds[builds.length - 1]; b?.resolve(null); },
    play: async (result, n) => { log.push(`play #${n} ${result.take}`); },
    stopPlayback: () => log.push('stop'),
    setTimer: (fn) => { const id = nextTimer++; timers.set(id, fn); return id; },
    clearTimer: (id) => timers.delete(id),
  };
  const fireTimers = () => { const all = [...timers.values()]; timers.clear(); all.forEach((fn) => fn()); };
  const finish = async (take = 'T') => {
    const b = builds.find((x) => !x.done);
    b.done = true;
    if (fail(b.job)) b.reject(new Error('AMP5000 broken')); else b.resolve({ take: `${take}:${b.job.text}` });
    await new Promise((r) => setTimeout(r, 0));
  };
  return { deps, log, fireTimers, finish, most: () => most, timers };
}

test('start builds at once and plays the result', async () => {
  const r = rig(), s = createListenSession(r.deps);
  const started = s.start({ text: 'a' });
  await r.finish();
  await started;
  assert.deepEqual(r.log, ['build a', 'play #1 T:a']);
  assert.equal(s.state().status, 'playing');
});

test('changes during typing are joined into one build after the pause', async () => {
  const r = rig(), s = createListenSession(r.deps);
  const started = s.start({ text: 'a' }); await r.finish(); await started;
  s.setJob({ text: 'b' }); s.setJob({ text: 'bc' }); s.setJob({ text: 'bcd' });
  assert.equal(r.timers.size, 1, 'one waiting pause');
  r.fireTimers();
  await r.finish();
  assert.deepEqual(r.log.slice(2), ['build bcd', 'play #2 T:bcd']);
});

test('while the new take loads, the session does not say building (it read "building #null")', async () => {
  let release;
  const s = createListenSession({
    build: async () => ({ take: 't' }), cancelBuild() {}, play: () => new Promise((r) => { release = r; }), stopPlayback() {},
    onChange() {}, setTimer: setTimeout, clearTimer: clearTimeout,
  });
  s.start({ key: 'a' });
  await new Promise((r) => setTimeout(r, 10));   // the build is done, the player is still loading
  const loading = s.state();
  assert.ok(!(loading.status === 'building' && loading.building === null), JSON.stringify(loading));
  assert.equal(loading.building, null);
  release();
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(s.state().status, 'playing');
});

test('a change during a build builds once more afterwards, never two builds at the same time', async () => {
  const r = rig(), s = createListenSession(r.deps);
  const started = s.start({ text: 'a' });
  s.setJob({ text: 'b' }, true);   // Ctrl+Enter while the first build runs
  s.setJob({ text: 'c' }, true);
  await r.finish();   // a finishes, but newer changes wait: not played
  await r.finish();   // c
  await started;
  assert.equal(r.most(), 1);
  assert.deepEqual(r.log, ['build a', 'build c', 'play #2 T:c']);
});

test('flush runs the waiting pause at once and does nothing when nothing waits', async () => {
  const r = rig(), s = createListenSession(r.deps);
  const started = s.start({ text: 'a' }); await r.finish(); await started;
  await s.flush();
  assert.equal(r.log.length, 2, 'nothing waited: no build');
  s.setJob({ text: 'b' });
  const flushed = s.flush();
  assert.equal(r.timers.size, 0);
  await r.finish(); await flushed;
  assert.deepEqual(r.log.slice(2), ['build b', 'play #2 T:b']);
});

test('a failed build keeps the last good build playing and says so', async () => {
  const r = rig({ fail: (job) => job.text === 'bad' }), s = createListenSession(r.deps);
  const started = s.start({ text: 'a' }); await r.finish(); await started;
  const again = s.setJob({ text: 'bad' }, true);
  await r.finish(); await again;
  const st = s.state();
  assert.equal(st.status, 'error');
  assert.equal(st.playing, 1);
  assert.match(st.error, /AMP5000/);
  assert.ok(!r.log.includes('stop'), 'the sound went on');
});

test('stop cancels the running build, stops the sound, and a late build is not played', async () => {
  const r = rig(), s = createListenSession(r.deps);
  const started = s.start({ text: 'a' }); await r.finish(); await started;
  s.setJob({ text: 'b' }, true);
  s.stop();
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(r.log.slice(2), ['build b', 'cancel', 'stop']);
  assert.equal(s.state().status, 'stopped');
  s.changed();
  assert.equal(r.timers.size, 0, 'a stopped session does not build on changes');
});

// ---- the loop arithmetic ----

test('bars: built from bar 1 to one bar past the range (decision 13); a fast draft builds the range alone, also one bar past', () => {
  assert.deepEqual(rangeBuild({ firstBar: 17, bars: 4 }), { build: { firstBar: 1, bars: 21 }, takeFirstBar: 1, before: 16, method: 'from-start' });
  assert.deepEqual(rangeBuild({ firstBar: 1, bars: 4 }), { build: { firstBar: 1, bars: 5 }, takeFirstBar: 1, before: 0, method: 'from-start' });
  assert.deepEqual(rangeBuild({ firstBar: 17, bars: 4 }, { fastDraft: true }), { build: { firstBar: 17, bars: 5 }, takeFirstBar: 17, before: 0, method: 'fast-draft' });
  assert.deepEqual(barsLoop({ firstBar: 17, bars: 4 }, 2, 15), { start: 4, end: 12 });
  assert.deepEqual(barsLoop({ firstBar: 17, bars: 4 }, 2), { start: 32, end: 40 });
});

test('a stretch of a WAV take: the byte range of its frames from the header, and a WAV of its own around those bytes', () => {
  // a 16-bit stereo WAV at 1000 Hz with a LIST chunk before the data: 10 s of frames numbered 0..9999 on the left channel
  const rate = 1000, frames = 10000, align = 4, list = 10;
  const head = new Uint8Array(12 + 24 + 8 + list + 8), v = new DataView(head.buffer);
  const put = (at, s) => [...s].forEach((c, i) => { head[at + i] = c.charCodeAt(0); });
  put(0, 'RIFF'); v.setUint32(4, head.length - 8 + frames * align, true); put(8, 'WAVE');
  put(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 2, true); v.setUint32(24, rate, true); v.setUint32(28, rate * align, true);
  v.setUint16(32, align, true); v.setUint16(34, 16, true);
  put(36, 'LIST'); v.setUint32(40, list, true);
  put(44 + list, 'data'); v.setUint32(48 + list, frames * align, true);
  const dataAt = head.length, total = dataAt + frames * align;
  const slice = wavSlice(head.buffer, total, 2.5, 4.0);
  assert.deepEqual([slice.from, slice.to, slice.offset], [dataAt + 2500 * align, dataAt + 4000 * align - 1, 2.5]);
  const body = new Uint8Array(slice.to - slice.from + 1), out = new DataView(slice.wav(body.buffer));
  assert.equal(out.getUint32(4, true), out.byteLength - 8);
  assert.equal(out.getUint32(dataAt - 4, true), 1500 * align);
  assert.equal(out.byteLength, dataAt + 1500 * align);
  assert.equal(wavSlice(new Uint8Array(40).buffer, 100, 0, 1), null, 'not a WAV: the caller reads the whole take');
  const clipped = wavSlice(head.buffer, total, 9.5, 12);   // past the end: up to the last frame
  assert.equal(clipped.to, total - 1);
});

function tone(rate, parts) {
  // parts: [[seconds, amplitude]]
  const total = parts.reduce((s, p) => s + p[0], 0), x = new Float32Array(Math.round(total * rate));
  let i = 0;
  for (const [sec, amp] of parts) for (let k = 0; k < Math.round(sec * rate); k++, i++) x[i] = amp * Math.sin(i / 3);
  return x;
}

test('silence: edges are trimmed with a margin, a short rest stays, a long gap is skipped only in gaps mode', () => {
  const rate = 1000, barSec = 2;
  const x = tone(rate, [[1, 0], [1, 0.5], [0.5, 0], [1, 0.5], [3, 0], [1, 0.5], [1, 0]]);   // rest of 0.5 s, gap of 3 s
  const w = silentWindows([x], rate), loop = { start: 0, end: 8.5 };
  const edges = playSegments(w, loop, { mode: 'edges', barSec });
  assert.equal(edges.segments.length, 1);
  assert.ok(Math.abs(edges.segments[0].from - 0.85) < 0.02 && Math.abs(edges.segments[0].to - 7.65) < 0.02);
  const gaps = playSegments(w, loop, { mode: 'gaps', barSec });
  assert.equal(gaps.segments.length, 2, 'the 0.5 s rest stays, the 3 s gap goes');
  assert.ok(Math.abs(gaps.skipped - (8.5 - heardLength(gaps.segments))) < 1e-9);
  assert.equal(playSegments(w, loop, { mode: 'gaps', barSec: 4 }).segments.length, 1, 'a gap shorter than one bar (4 s) stays');
  const soft = tone(rate, [[2, 0.002]]);   // about -57 dBFS RMS: quiet, not silent
  assert.equal(playSegments(silentWindows([soft], rate), { start: 0, end: 2 }, { mode: 'gaps' }).silent, false);
  const silent = playSegments(silentWindows([new Float32Array(2000)], rate), { start: 0, end: 2 }, { mode: 'gaps' });
  assert.equal(silent.silent, true); assert.deepEqual(silent.segments, [{ from: 0, to: 2 }]);
});

test('preview time and take time map both ways across skipped gaps', () => {
  const segments = [{ from: 1, to: 3 }, { from: 6, to: 7 }];
  assert.equal(takeTimeAt(segments, 0.5), 1.5);
  assert.equal(takeTimeAt(segments, 2.5), 6.5);
  assert.equal(takeTimeAt(segments, 3.5), 1.5, 'wraps');
  assert.equal(previewTimeAt(segments, 6.5), 2.5);
  assert.equal(previewTimeAt(segments, 4), 2, 'inside a gap: the start of the next segment');
});

// ---- the marks ----

const song = `composition "x" { ample 1  tempo 120  master-seed 1 }
midi-clip theme at 3 for 2 {
  notes 1:0:1:60:100 1:1:1:64:100 2:0:4:67:90;tie=1
}
midi-clip up {
  notes theme.notes |> transpose(12)
}
synth lead {
  from up.notes -> notes
}
`;

test('a block span runs from its first line to its closing brace; braces in strings and comments are skipped', () => {
  const span = blockSpan(song, 'theme');
  assert.equal(song.slice(span.from, span.from + 9), 'midi-clip');
  assert.equal(song[span.to - 1], '}');
  const tricky = 'synth a {\n  label "a { b"  # }\n}\n';
  assert.equal(blockSpan(tricky, 'a').to, tricky.length - 1);
  assert.equal(blockSpan(song, 'nope'), null);
});

test('literal notes sound at the playhead (clip-relative bars); a pipeline gets a line mark, never single notes', () => {
  const span = blockSpan(song, 'theme'), [line] = notesLines(song, span);
  assert.equal(line.literal, true);
  const at = soundingTokens(song, line, 3.3, { clipFirstBar: 3 });   // clip bar 1.3 = beat 1.2: the second note (beat 1..2)
  assert.deepEqual(at.map((m) => song.slice(m.from, m.to)), ['1:1:1:64:100']);
  assert.deepEqual(soundingTokens(song, line, 4.5, { clipFirstBar: 3 }).map((m) => song.slice(m.from, m.to)), ['2:0:4:67:90;tie=1']);
  const marks = playbackMarks({ text: song, target: 'up', songBar: 3.3, clipFirstBar: (n) => (n === 'theme' ? 3 : 1), active: ['theme'] });
  assert.ok(marks.some((m) => m.cls === 'lp-target' && song.slice(m.from, m.from + 12) === 'midi-clip up'));
  assert.ok(marks.some((m) => m.cls === 'lp-line' && /\|>/.test(song.slice(m.from, m.to))));
  assert.ok(marks.some((m) => m.cls === 'lp-now' && song.slice(m.from, m.to) === '1:1:1:64:100'));
  assert.ok(!marks.some((m) => m.cls === 'lp-now' && /\|>/.test(song.slice(m.from, m.to))));
});

test('marks move with lines that moved; an edited line loses its mark', () => {
  const marks = playbackMarks({ text: song, target: 'theme', songBar: 3.3, clipFirstBar: () => 3 });
  const inserted = `# a new comment\n${song}`;
  const moved = moveMarks(marks, song, inserted);
  assert.ok(moved.some((m) => m.cls === 'lp-now' && inserted.slice(m.from, m.to) === '1:1:1:64:100'));
  const edited = song.replace('1:1:1:64:100', '1:1:1:65:100');
  const after = moveMarks(marks, song, edited);
  assert.ok(!after.some((m) => m.cls === 'lp-now'), 'the edited notes line is not marked');
  assert.ok(!after.some((m) => m.cls === 'lp-target'), 'a block span with an edited line is not marked as playing');
});

// WP6: chords, melody, drums and auto(...) follow the engine's timing (Renderers.ChordBars, MelodyWithVelocity, DrumSong.NameAtBar, Automation.ParseSamples)
const words = `composition "w" { ample 1  tempo 120  master-seed 1 }
midi-clip prog at 1 for 8 {
  chords Am F.G C
}
midi-clip tune at 1 for 4 {
  melody "mp c4:2 < r d4 e4:3 ff g4:1/8"
  chords Am F
}
drums kit1 at 1 for 8 {
  pattern kick:x...x...x...x... snare:....x.......x...
  patterns fill=kick:xxxxxxxxxxxxxxxx / long=kick:x...............x...............
  song pattern*2 fill long
}
drums named at 1 for 4 {
  pattern beat-B
}
drums sampled at 1 for 4 {
  kit samples/kit
  song pattern*2 fill
}
tremolo gate {
  pattern "x.xx.xx.x.xx.x.x"
}
filter sweep {
  cutoff auto(1:200, 5.5:2000, 9:8000)
  resonance auto(2s:0, 8s:90)
}
`;
const cut = (marks) => marks.map((m) => words.slice(m.from, m.to));

test('chords: one per bar of the request, repeating; a dot shares the bar', () => {
  const span = blockSpan(words, 'prog');
  assert.deepEqual(cut(chordMarks(words, span, 1.5)), ['Am']);
  assert.deepEqual(cut(chordMarks(words, span, 2.2)), ['F']);   // first half of F.G
  assert.deepEqual(cut(chordMarks(words, span, 2.7)), ['G']);
  assert.deepEqual(cut(chordMarks(words, span, 4.1)), ['Am']);   // the progression repeats
});

test('melody: markers take no time, rests mark nothing, a note stops at its bar end, an unread length is one beat; melody wins over chords', () => {
  const span = blockSpan(words, 'tune');
  assert.deepEqual(cut(melodyMarks(words, span, 1.1)), ['c4:2']);
  assert.deepEqual(cut(melodyMarks(words, span, 1.6)), []);   // beat 2..3: the rest
  assert.deepEqual(cut(melodyMarks(words, span, 1.8)), ['d4']);
  assert.deepEqual(cut(melodyMarks(words, span, 2.1)), ['e4:3']);
  assert.deepEqual(cut(melodyMarks(words, span, 2.8)), ['g4:1/8']);   // "1/8" is no number for the engine: one beat
  assert.deepEqual(cut(melodyMarks(words, span, 3.5)), []);   // after the melody: nothing, no repeat
  assert.deepEqual(cut(melodyMarks(words, span, 3.1, 3)), ['c4:2']);   // a frame that starts at bar 3 (the function is pure; Live Preview passes first 1, plan 6.4)
  const marks = playbackMarks({ text: words, target: 'tune', songBar: 1.6 });
  assert.ok(!marks.some((m) => m.cls === 'lp-now' && ['Am', 'F'].includes(words.slice(m.from, m.to))), 'a melody clip does not play its chords');
});

test('drums: the section of the bar and the step of each voice grid; a grid longer than a bar, a sample kit and other blocks get no step marks', () => {
  const span = blockSpan(words, 'kit1');
  assert.deepEqual(cut(drumMarks(words, span, 1.0)), ['pattern*2', 'x', '.']);   // step 0 of kick and snare
  assert.deepEqual(cut(drumMarks(words, span, 2.25)), ['pattern*2', 'x', 'x']);   // step 4
  const fill = drumMarks(words, span, 3.5);
  assert.equal(words.slice(fill[0].from, fill[0].to), 'fill');
  assert.equal(fill.length, 2);
  assert.equal(words.slice(fill[1].from - 18, fill[1].from), 'fill=kick:xxxxxxxx');   // step 8 of the fill grid
  assert.deepEqual(cut(drumMarks(words, span, 4.5)), ['long']);   // 32 steps: only the section
  assert.deepEqual(cut(drumMarks(words, span, 5.0)), ['pattern*2', 'x', '.']);   // the song repeats from the first bar
  assert.deepEqual(cut(drumMarks(words, blockSpan(words, 'named'), 2.3)), ['beat-B']);
  assert.deepEqual(drumMarks(words, blockSpan(words, 'sampled'), 1.5), []);
  assert.deepEqual(drumMarks(words, blockSpan(words, 'gate'), 1.5), []);
});

test('auto: the two points around the playhead, in bars (with decimals) or seconds from the first bar of the request', () => {
  const span = blockSpan(words, 'sweep');
  assert.deepEqual(cut(autoMarks(words, span, 3, 1, 2)), ['1:200', '5.5:2000', '2s:0', '8s:90']);   // bar 3 = 4 s: between 2 s and 8 s too
  assert.deepEqual(cut(autoMarks(words, span, 6, 1, 2)).slice(0, 2), ['5.5:2000', '9:8000']);
  assert.deepEqual(cut(autoMarks(words, span, 12, 1, 2)).slice(0, 1), ['9:8000']);   // after the last point: the last one only
  assert.deepEqual(cut(autoMarks(words, span, 1.2, 1, 2)).slice(2), ['2s:0']);   // before 2 s (bar 2): the first point only
  assert.deepEqual(cut(autoMarks(words, span, 7, 5, 2)).slice(0, 2), ['1:200', '5.5:2000']);   // a request from bar 5: point 5.5 is song bar 9.5
});

test('the frame of a block that is not placed: its bars count from its origin', () => {
  const marks = playbackMarks({ text: words, target: 'prog', songBar: 6.5, frame: () => ({ first: 1, offset: 4 }) });   // origin bar 5: song bar 6.5 is its bar 2.5
  assert.ok(marks.some((m) => m.cls === 'lp-now' && words.slice(m.from, m.to) === 'G'));
});
