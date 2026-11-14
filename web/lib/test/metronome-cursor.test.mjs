import test from 'node:test';
import assert from 'node:assert/strict';
import { createMetronomeCursor } from '../metronome-cursor.mjs';
import { metronomeClicks } from '../lite-roll-model.mjs';

const PPQ = 960;
const LOOKAHEAD_S = 0.3, ROUND_S = 0.08;

/**
 * Replays the Jam: a player clock (seconds), rounds every 80 ms (jittered), a playhead that is only refreshed on a frame tick
 * (`frameHz`, so it is stale between frames) or read fresh, and the panel's maths (`at = now + offset * secondsPerTick`).
 * Returns every scheduled click as { at, accent }.
 */
function replay({ frameHz = 60, bpm = () => 120, seconds = 20, loop = [0, 0], from = 0, stale = false }) {
  const [loopStart, loopEnd] = loop, span = loopEnd > loopStart ? loopEnd - loopStart : 0;
  const cursor = createMetronomeCursor({ ppq: PPQ, loopStart, loopEnd });
  const STEP = 0.001, table = [from];                // ticks played at every millisecond, with the tempo of that moment
  for (let i = 1; i <= (seconds + 1) / STEP; i++) table.push(table[i - 1] + STEP * bpm((i - 1) * STEP) / 60 * PPQ);
  const posAt = (x) => { const ticks = table[Math.round(x / STEP)]; return span ? loopStart + ((ticks - loopStart) % span) : ticks; };
  const scheduled = [];
  let frameHead = posAt(0), nextFrame = 0;
  for (let i = 0, t = 0; t < seconds; i++) {
    t = i * ROUND_S + ((i * 7919) % 13) / 1000;      // uneven rounds
    for (; nextFrame <= t; nextFrame += 1 / frameHz) frameHead = posAt(nextFrame);
    const pos = stale ? frameHead : posAt(t), secondsPerTick = 60 / bpm(t) / PPQ;
    for (const c of cursor.round(pos, LOOKAHEAD_S / secondsPerTick)) {
      scheduled.push({ at: t + c.offset * secondsPerTick, accent: c.accent });
    }
  }
  return scheduled;
}

const gaps = (list) => list.slice(1).map((c, i) => c.at - list[i].at);

for (const [frameHz, bpm] of [[60, 120], [30, 120], [60, 90], [30, 90], [20, 120]]) {
  test(`one click per beat with a ${frameHz} Hz playhead at ${bpm} bpm`, () => {
    const clicks = replay({ frameHz, bpm: () => bpm, stale: true, seconds: 20 });
    const beat = 60 / bpm;
    assert.ok(Math.abs(clicks.length - 20 / beat) <= 2, `${clicks.length} clicks for ${20 / beat} beats`);
    // a stale playhead moves the audio time by up to one frame, never a second click
    for (const g of gaps(clicks)) assert.ok(g > beat * 0.8, `flam: gap ${g}`);
    assert.equal(clicks.filter((c) => c.accent).length, Math.ceil(clicks.length / 4));
  });
}

test('the playhead read fresh from the player gives clicks on the beat grid', () => {
  const clicks = replay({ stale: false, seconds: 10 });
  clicks.forEach((c, i) => assert.ok(Math.abs(c.at - i * 0.5) < 1e-6, `${i}: ${c.at}`));
});

test('a loop wrap neither doubles nor drops the beat at the seam', () => {
  const loop = [0, 4 * PPQ];                          // 2 s at 120 bpm
  const clicks = replay({ loop, stale: true, frameHz: 30, seconds: 12 });
  const gapList = gaps(clicks);
  assert.ok(gapList.every((g) => Math.abs(g - 0.5) < 0.2 && g > 0.4), `gaps ${gapList.map((g) => g.toFixed(3))}`);
  assert.equal(clicks.length, 25);   // beats 0..24 of a 12 s run at 120 bpm, each once
  // beat one of every lap is the accent
  assert.deepEqual(clicks.slice(0, 8).map((c) => c.accent), [true, false, false, false, true, false, false, false]);
});

test('a loop that does not start at 0 and a start inside the loop', () => {
  const clicks = replay({ loop: [2 * PPQ, 6 * PPQ], from: 3 * PPQ, stale: true, seconds: 10 });
  assert.ok(gaps(clicks).every((g) => g > 0.4));
  assert.equal(clicks.filter((c) => c.accent).length > 0, true);
});

test('a tempo change keeps one click per beat (no replay of a beat when the tick position jumps back)', () => {
  const bpm = (t) => (t < 6 ? 120 : 80);
  const clicks = replay({ bpm, stale: true, seconds: 14 });
  const keys = clicks.map((c) => c.at);
  for (let i = 1; i < keys.length; i++) assert.ok(keys[i] - keys[i - 1] > 0.3, `flam at ${keys[i]}`);
  assert.ok(clicks.length >= 14 / 0.75 - 4);
});

test('restart and pause/resume: a new cursor starts clean, the old one never carries over', () => {
  const first = createMetronomeCursor({ ppq: PPQ });
  assert.deepEqual(first.round(0, 2 * PPQ).map((c) => c.offset), [0, PPQ]);
  assert.deepEqual(first.round(0, 2 * PPQ), []);      // same window again: already handed out
  const resumed = createMetronomeCursor({ ppq: PPQ });
  assert.deepEqual(resumed.round(PPQ, 2 * PPQ).map((c) => c.offset), [0, PPQ]);   // resumed at beat 1: beat 1 clicks again
  const seekedBack = createMetronomeCursor({ ppq: PPQ });
  assert.equal(seekedBack.round(0, PPQ / 2).length, 1);
});

test('the same beat seen from two slightly different positions is handed out once', () => {
  const cursor = createMetronomeCursor({ ppq: PPQ });
  assert.equal(cursor.round(PPQ - 40, 100).length, 1);   // beat 1 is 40 ticks ahead
  assert.equal(cursor.round(PPQ - 20, 100).length, 0);   // 20 ticks closer, same beat
  assert.equal(cursor.round(PPQ + 5, 100).length, 0);    // just past it
});

test('the earlier approach (compare audio times with 10 ms) double-schedules with a stale playhead; the cursor does not', () => {
  const old = (frameHz) => {
    let until = 0, n = 0, head = 0, nextFrame = 0;
    for (let i = 0; i * ROUND_S < 60; i++) {
      const t = i * ROUND_S + ((i * 7919) % 13) / 1000;
      for (; nextFrame <= t; nextFrame += 1 / frameHz) head = nextFrame * 2 * PPQ;
      const secondsPerTick = 0.5 / PPQ;
      for (const c of metronomeClicks(head, 0.3 / secondsPerTick, PPQ)) {
        const at = t + c.offset * secondsPerTick;
        if (at <= until + 0.01) continue;
        until = at;
        n++;
      }
    }
    return n;
  };
  assert.ok(old(60) > 125 && old(30) > 190, 'the old comparison flams');   // 121 beats in 60 s
  assert.equal(replay({ frameHz: 30, stale: true, seconds: 60 }).length, 121);
  assert.equal(replay({ frameHz: 60, stale: true, seconds: 60 }).length, 121);
});

test('a big step back of the position (tempo drop) lets the beats ahead sound again instead of going silent', () => {
  const cursor = createMetronomeCursor({ ppq: PPQ });
  assert.equal(cursor.round(20 * PPQ, PPQ).length, 1);
  assert.equal(cursor.round(20 * PPQ - 6 * PPQ, PPQ).length, 1);   // jumped back six beats: the beat at the new position sounds
  assert.equal(cursor.round(20 * PPQ - 6 * PPQ + 10, 500).length, 0);
});
