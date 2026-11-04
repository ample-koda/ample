import test from 'node:test';
import assert from 'node:assert/strict';
import { createVibe, stepVibe, vibePose } from '../koda-vibe.mjs';
import { newStrand, nearestOnRect, stepStrand, strandPath } from '../koda-strand.mjs';
import { glanced, namedBlocks } from '../koda-glance.mjs';

const DT = 1000 / 60;
// a click every 500 ms (120 bpm) on top of a soft bed
const clicks = (ms) => { const s = createVibe(); const beats = []; for (let t = 0; t < ms; t += DT) { const phase = t % 500; stepVibe(s, phase < 40 ? 0.6 : 0.06, DT); if (s.onset) beats.push(t); } return { s, beats }; };

test('silence does not move Koda', () => {
  const s = createVibe();
  for (let i = 0; i < 120; i++) stepVibe(s, 0.001, DT);
  assert.equal(s.active, false);
  assert.ok(s.amp < 0.01);
});

test('a steady beat is found, with its pace, and Koda starts to move', () => {
  const { s, beats } = clicks(4000);
  assert.equal(s.active, true);
  assert.ok(s.amp > 0.9);
  assert.ok(beats.length >= 6 && beats.length <= 9, `beats ${beats.length}`);
  assert.ok(Math.abs(s.period - 500) < 70, `period ${s.period}`);
});

test('it starts and stops with the music', () => {
  const s = createVibe();
  let started = 0, stopped = 0;
  for (let t = 0; t < 1500; t += DT) { stepVibe(s, (t % 500) < 40 ? 0.6 : 0.06, DT); if (s.started) started++; }
  for (let t = 0; t < 2500; t += DT) { stepVibe(s, 0, DT); if (s.stopped) stopped++; }
  assert.equal(started, 1);
  assert.equal(stopped, 1);
  assert.equal(s.active, false);
  assert.ok(s.amp < 0.05);
});

test('sound without a beat keeps a soft pace', () => {
  const s = createVibe();
  let pulses = 0;
  for (let t = 0; t < 5000; t += DT) { stepVibe(s, 0.2, DT); if (s.pulse > 0.5 && s.t - s.last < DT * 1.5) pulses++; }
  assert.equal(s.active, true);
  assert.ok(pulses >= 2);
});

test('a busy Koda moves less than one that does nothing', () => {
  const s = createVibe(); s.amp = 1; s.pulse = 1; s.lean = 1;
  const full = vibePose(s, 'full'), soft = vibePose(s, 'soft');
  assert.ok(Math.abs(soft.lift) < Math.abs(full.lift) / 3);
  assert.ok(Math.abs(soft.lean) < Math.abs(full.lean));
});

test('a line hangs below the straight line and sways when an end moves', () => {
  const a = { x: 0, y: 0 }, b = { x: 300, y: 0 };
  const s = newStrand(a, b);
  for (let i = 0; i < 200; i++) stepStrand(s, a, b, DT);
  assert.ok(Math.abs(s.x - 150) < 1 && s.y > 30 && s.y < 90);
  const still = s.y;
  b.y = 120;
  stepStrand(s, a, b, DT);
  assert.notEqual(s.y, still);
  assert.match(strandPath(a, b, s), /^M0 0Q[\d.-]+ [\d.-]+ 300 120$/);
});

test('a line ends in the middle of the side that faces it, or in the middle of the block from inside', () => {
  const r = { left: 100, top: 100, right: 200, bottom: 160 };
  assert.deepEqual(nearestOnRect(r, { x: 20, y: 130 }), { x: 100, y: 130 });
  assert.deepEqual(nearestOnRect(r, { x: 150, y: 130 }), { x: 150, y: 130 });
  assert.deepEqual(nearestOnRect(r, { x: 130, y: 20 }), { x: 150, y: 100 });
  assert.deepEqual(nearestOnRect(r, { x: 900, y: 100 }), { x: 200, y: 130 });
});

test('the blocks that a task looks at are taken out of its log, once, and only from tools and commands', () => {
  assert.deepEqual(namedBlocks('read piano and piano_reverb, not pianos', ['piano', 'piano_reverb', 'drums']), ['piano', 'piano_reverb']);
  const log = [{ seq: 1, kind: 'assistant', text: 'drums sound good' }, { seq: 2, kind: 'tool', text: 'ample_get_block (block=drums): completed' }, { seq: 3, kind: 'command', text: 'ran: grep piano song.amplet' }, { seq: 4, kind: 'tool', text: 'ample_get_block (block=drums): completed' }];
  assert.deepEqual(glanced(log, ['piano', 'drums', 'bass']), { blocks: ['drums', 'piano'], seq: 4 });
  assert.deepEqual(glanced(log, ['piano', 'drums', 'bass'], 3), { blocks: ['drums'], seq: 4 });
  assert.deepEqual(glanced([], ['bass'], 0, 'thinking about bass'), { blocks: ['bass'], seq: 0 });
});

test('a fast beat does not make a frantic Koda: smaller bounces, a lean every second beat', async () => {
  const { easeFast } = await import('../koda-vibe.mjs');
  assert.equal(easeFast(600), 1);
  assert.ok(easeFast(250) <= 0.55);
  // 240 bpm clicks: the lean flips every second beat
  const s = createVibe(); let flips = 0, last = s.dir, onsets = 0;
  for (let t = 0; t < 4000; t += DT) { stepVibe(s, (t % 250) < 40 ? 0.6 : 0.06, DT); if (s.onset) onsets++; if (s.dir !== last) { flips++; last = s.dir; } }
  assert.ok(onsets >= 8 && flips <= onsets / 2 + 1, `onsets ${onsets} flips ${flips}`);
});

test('a line to a long thin row ends level with its middle at the side that faces it, not on the edge between two rows', async () => {
  const { nearestOnRect } = await import('../koda-strand.mjs');
  const row = { left: 0, right: 1000, top: 100, bottom: 130 };
  assert.deepEqual(nearestOnRect(row, { x: 1100, y: 500 }), { x: 1000, y: 115 });
  assert.deepEqual(nearestOnRect(row, { x: -50, y: 20 }), { x: 0, y: 115 });
});
