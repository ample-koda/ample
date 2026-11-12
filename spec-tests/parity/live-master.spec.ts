import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 3.1: the live master is the limiter of the render plus a BS.1770 loudness meter.
freshServer();
test('@audio the master never passes its ceiling and measures loudness', async ({ page }) => {
  await openApp(page, { reset: false });
  const r = await page.evaluate(async () => {
    const { liveEffectGraph, prepareWorklets } = await import('/app/studio/preview/audio.js');
    const rate = 48000, frames = rate * 4;
    const run = async (amplitude: number, limiter: string) => {
      const context = new OfflineAudioContext(2, frames, rate); await prepareWorklets(context);
      const buffer = context.createBuffer(2, frames, rate);
      let seed = 7; const noise = Float32Array.from({ length: frames }, (_, i) => { seed = (seed * 1664525 + 1013904223) >>> 0; return amplitude * (Math.sin(2 * Math.PI * 997 * i / rate) * .6 + ((seed / 2 ** 32) * 2 - 1) * .4); });
      buffer.copyToChannel(noise, 0); buffer.copyToChannel(noise, 1);
      const source = context.createBufferSource(), out = context.createGain(); source.buffer = buffer; out.connect(context.destination);
      const graph = liveEffectGraph(context, source, out, 'master', { limiter, target: '-14 LUFS' }, 120, null);
      source.start();
      const result = await context.startRendering();
      await new Promise((resolve) => setTimeout(resolve, 300));   // the last loudness message
      const left = result.getChannelData(0); let peak = 0; for (let i = rate * .5; i < frames; i++) peak = Math.max(peak, Math.abs(left[i]));
      const loud = graph.loudness.read(); graph.dispose();
      return { peak, loud, latency: graph.latency };
    };
    return { hot: await run(1.6, '-6 dB'), quiet: await run(.1, '-1 dB') };
  });
  expect(r.hot.peak, 'peak under the -6 dB ceiling').toBeLessThan(10 ** (-6 / 20) + 0.012);
  expect(r.hot.peak).toBeGreaterThan(10 ** (-6 / 20) * .8);
  expect(r.hot.latency).toBeGreaterThan(0);
  // a quiet signal is left alone and read by the meter: sine .06 + noise .04 at amplitude .1 is about -20 LUFS; compare with the node-side meter run by the same core
  expect(Number.isFinite(r.quiet.loud.momentary)).toBe(true);
  expect(r.quiet.loud.momentary).toBeLessThan(-18); expect(r.quiet.loud.momentary).toBeGreaterThan(-26);
});
