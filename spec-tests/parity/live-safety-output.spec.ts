import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Live output quality: nested mixers with positive gains sum past 1.0; the output stage must keep every sample under 0.98 and leave a take at -1 dBFS untouched.
freshServer();
const probe = (page: any, gainDb: number, amp: number) => page.evaluate(async ({ gainDb, amp }: any) => {
  const { mixerGraph } = await import('/app/../lib/live-mixer.mjs');
  const { playbackOutput } = await import('/app/playback-volume.js');
  const rate = 44100, frames = rate * 2, context = new OfflineAudioContext(2, frames, rate);
  const buffer = context.createBuffer(2, frames, rate);
  for (let c = 0; c < 2; c++) { const d = buffer.getChannelData(c); for (let i = 0; i < frames; i++) d[i] = amp * (Math.sin(i * 2 * Math.PI * 220 / rate) + .5 * Math.sin(i * 2 * Math.PI * 3300 / rate)) / 1.5; }
  const inner = mixerGraph(context, 4), outer = mixerGraph(context, 4, { bus: true });
  for (let k = 0; k < 4; k++) { const s = context.createBufferSource(); s.buffer = buffer; s.connect(inner.lanes[k]); s.start(); inner.setLane(k, { gainDb, pan: 0 }); }
  inner.output.connect(outer.lanes[0]); outer.setLane(0, { gainDb: 0, pan: 0 }); outer.output.connect(playbackOutput(context));
  const out = await context.startRendering(); let peak = 0, over = 0, nan = 0, click = 0, prev = 0;
  const d = out.getChannelData(0);
  for (let i = 2000; i < frames; i++) { const v = d[i]; if (!Number.isFinite(v)) nan++; const a = Math.abs(v); if (a > peak) peak = a; if (a >= 0.98 + 1e-6) over++; if (Math.abs(v - prev) > 0.5) click++; prev = v; }
  return { peak, over, nan, click };
}, { gainDb, amp });
test('@audio a hot live sum stays under 0.98 and has no hard-clip jumps', async ({ page }) => {
  await openApp(page, { reset: false });
  const hot = await probe(page, 6, 0.9);
  console.log('hot', JSON.stringify(hot));
  expect(hot.nan).toBe(0); expect(hot.over).toBe(0); expect(hot.peak).toBeGreaterThan(0.9);
});
test('@audio a take at -1 dBFS passes the output stage unchanged', async ({ page }) => {
  await openApp(page, { reset: false });
  const calm = await probe(page, 0, 0.222);   // four lanes of 0.222 sum to ~0.89 at most
  console.log('calm', JSON.stringify(calm));
  expect(calm.peak).toBeLessThanOrEqual(0.891 + 0.01); expect(calm.over).toBe(0);
});
test('@audio a moderately hot sum (about +3 dB over full scale) is shaped without clicks', async ({ page }) => {
  await openApp(page, { reset: false });
  const warm = await probe(page, -3, 0.5);   // four lanes at -3 dB of 0.5 sum to about 1.4 before the stage
  console.log('warm', JSON.stringify(warm));
  expect(warm.over).toBe(0); expect(warm.click).toBeLessThanOrEqual(2); expect(warm.peak).toBeGreaterThan(0.93);
});
