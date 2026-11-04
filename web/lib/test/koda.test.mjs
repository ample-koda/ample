import test from 'node:test';
import assert from 'node:assert/strict';
import { SEGMENTS, LENGTH, TAILS, createTail, stepTail } from '../koda-tail.mjs';
import { clampSpot, placeBubble, tetherFor } from '../koda-placement.mjs';
import * as P from '../koda-presence.mjs';

const settle = (cfg, steps = 600) => { const tail = createTail(100, 50); for (let i = 0; i < steps; i++) stepTail(tail, { bx: 100, by: 50, cfg, still: true }); return tail.pts; };

test('the tail keeps its length and hangs from its base', () => {
  const pts = settle(TAILS.neutral);
  assert.equal(pts[0].x, 100);
  for (let i = 1; i <= SEGMENTS; i++) assert.ok(Math.abs(Math.hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y) - LENGTH) < 0.01);
});

test('a sad tail hangs lower than an excited one, and an angry tail stands up', () => {
  const tip = (cfg) => settle(cfg)[SEGMENTS].y;
  assert.ok(tip(TAILS.sad) > tip(TAILS.neutral));
  assert.ok(tip(TAILS.angry) < tip(TAILS.sad));
  assert.ok(tip(TAILS.angry) < 50);   // above its base
});

test('when the picture moves, the tail stays behind and then follows', () => {
  const tail = createTail(100, 50);
  for (let i = 0; i < 300; i++) stepTail(tail, { bx: 100, by: 50, cfg: TAILS.neutral, still: true });
  const before = tail.pts[SEGMENTS].x;
  stepTail(tail, { bx: 100, by: 50, dx: 20, cfg: TAILS.neutral, still: true });
  assert.ok(tail.pts[SEGMENTS].x < before - 10);
  for (let i = 0; i < 300; i++) stepTail(tail, { bx: 100, by: 50, cfg: TAILS.neutral, still: true });
  assert.ok(Math.abs(tail.pts[SEGMENTS].x - before) < 1);
});

test('reduced motion has no wave: the same step twice gives the same tail', () => {
  const a = createTail(0, 0), b = createTail(0, 0);
  for (let i = 0; i < 50; i++) { stepTail(a, { bx: 0, by: 0, t: i * 16, cfg: TAILS.excited, still: true }); stepTail(b, { bx: 0, by: 0, t: i * 16 + 777, cfg: TAILS.excited, still: true }); }
  assert.deepEqual(a.pts.map((p) => [p.x.toFixed(3), p.y.toFixed(3)]), b.pts.map((p) => [p.x.toFixed(3), p.y.toFixed(3)]));
});

test('a bubble goes above Koda, under Koda at the top of the container, and stays inside', () => {
  const container = { width: 900, height: 600 }, bubble = { width: 300, height: 140 };
  const low = placeBubble({ pet: { left: 700, top: 440, width: 150, height: 140 }, container, bubble });
  assert.equal(low.below, false);
  assert.ok(low.top < 440 && low.left + 300 <= 894);
  const high = placeBubble({ pet: { left: 300, top: 40, width: 150, height: 140 }, container, bubble });
  assert.equal(high.below, true);
  assert.ok(high.top > 40 + 140 * 0.9);
  assert.ok(low.tail >= 26 && low.tail <= 274);
});

test('a bubble that stays behind turns its tail to Koda', () => {
  const t = tetherFor({ pet: { left: 100, top: 80, width: 150, height: 140 }, container: { width: 900, height: 600 }, bubble: { width: 300, height: 140 }, at: { left: 400, top: 400 } });
  assert.equal(t.below, true);   // Koda is above the bubble: the tail is on its top edge
  assert.ok(t.to.y < t.from.y);
});

test('Koda keeps inside the container with room above', () => {
  assert.deepEqual(clampSpot({ left: -20, top: 0 }, { width: 150, height: 140 }, { width: 900, height: 600 }), { left: 0, top: 46 });
  assert.deepEqual(clampSpot({ left: 2000, top: 2000 }, { width: 150, height: 140 }, { width: 900, height: 600 }), { left: 750, top: 488 });
});

test('hiding, peeking, appearing and muting follow their rules', () => {
  let s = P.initial();
  assert.equal(P.mayOpenBubble(s), true);
  s = P.hide(s); assert.equal(s.presence, 'hiding');
  s = P.hidden(s); assert.equal(s.presence, 'hidden');
  assert.equal(P.animating(s), false);
  s = P.ready(s); assert.equal(s.presence, 'peeking'); assert.equal(s.pending, true);
  assert.equal(P.mayOpenBubble(s), false);
  s = P.show(s); assert.equal(s.presence, 'appearing');
  s = P.appeared(s); assert.equal(s.presence, 'visible');
  s = P.mute(s); assert.equal(P.mayOpenBubble(s), false);
  assert.equal(P.mayOpenBubble(P.unmute(s)), true);
  // hiding with something pending ends in peeking
  let t = P.ready(P.initial()); t = P.hidden(P.hide(t)); assert.equal(t.presence, 'peeking');
  // what is saved
  assert.deepEqual(P.toSaved(P.mute(P.hidden(P.hide(P.initial({ spot: { left: 1, top: 2 } }))))), { hidden: true, muted: true, spot: { left: 1, top: 2 } });
  assert.equal(P.initial({ hidden: true }).presence, 'hidden');
});
