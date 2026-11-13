import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 4.1 / 5.2 measurements: how much of real time each live block needs (an OfflineAudioContext renders as fast as it can, so render time / audio time is the CPU share of
// one core for stereo at 48 kHz) and how much sound latency a block reports. The numbers are printed (see PROGRESS.md) and bounded loosely so a regression shows.
freshServer();
test('live blocks need a small part of real time and report their latency', async ({ page }) => {
  test.setTimeout(180000);
  await openApp(page, { reset: false });
  const rows = await page.evaluate(async () => {
    const { liveEffectGraph, prepareWorklets } = await import('/app/studio/preview/audio.js');
    const { testSignal } = await import('/app/live-parity.js');
    const rate = 48000, seconds = 6, signal = testSignal(seconds, rate), frames = rate * seconds, out: any[] = [];
    const impulse = (ctx: BaseAudioContext) => { const b = ctx.createBuffer(2, rate, rate); for (let c = 0; c < 2; c++) b.copyToChannel(Float32Array.from({ length: rate }, (_, i) => Math.sin(i * (c ? 3.1 : 2.7)) * Math.exp(-i / 6000)), c); return b; };
    for (const type of ['reverb', 'gate', 'limiter', 'deesser', 'compressor', 'distortion', 'delay', 'eq', 'filter', 'chorus', 'master', 'convolution']) {
      const ctx = new OfflineAudioContext(2, frames, rate); await prepareWorklets(ctx);
      const buffer = ctx.createBuffer(2, frames, rate); signal.channels.forEach((c: Float32Array, i: number) => buffer.copyToChannel(c, i));
      const source = ctx.createBufferSource(), sink = ctx.createGain(); source.buffer = buffer; sink.connect(ctx.destination);
      const graph = liveEffectGraph(ctx, source, sink, type, {}, 120, type === 'convolution' ? impulse(ctx) : null); source.start();
      const t0 = performance.now(); await ctx.startRendering(); const ms = performance.now() - t0; graph.dispose();
      out.push({ type, cpuPercent: +(ms / (seconds * 1000) * 100).toFixed(1), latencyMs: +((graph.latency || 0) * 1000).toFixed(2) });
    }
    return out;
  });
  console.log('LIVE CPU/LATENCY', JSON.stringify(rows));
  for (const row of rows) expect(row.cpuPercent, `${row.type} cpu`).toBeLessThan(60);
  expect(rows.find((r: any) => r.type === 'limiter').latencyMs).toBeCloseTo(5, 0);
});
