import { test, expect } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();

// Local preview 5.1: the live chain bar says how late the sound is, and the figure is the delay that really exists in the graph.
test('the look-ahead of a limiter is the delay of an impulse through the live graph, and the bar adds it to the context latency', async ({ page }) => {
  await openApp(page, { reset: false });
  const measured = await page.evaluate(async () => {
    const { liveEffectGraph, prepareWorklets } = await import('/app/studio/preview/audio.js');
    const rate = 48000, context = new OfflineAudioContext(2, rate, rate);
    await prepareWorklets(context);
    const impulse = context.createBuffer(2, rate, rate); impulse.getChannelData(0)[1000] = .25; impulse.getChannelData(1)[1000] = .25;
    const source = context.createBufferSource(); source.buffer = impulse;
    const graph = liveEffectGraph(context, source, context.destination, 'limiter', { lookahead: '5', ceiling: '-1 dB' }, 120);
    source.start();
    const out = (await context.startRendering()).getChannelData(0);
    let first = -1;
    for (let index = 0; index < out.length; index++) if (Math.abs(out[index]) > .01) { first = index; break; }
    return { latency: graph.latency, delayed: (first - 1000) / rate };
  });
  expect(measured.latency).toBeCloseTo(.005, 5);
  expect(Math.abs(measured.delayed - measured.latency)).toBeLessThan(1 / 48000 * 2);
});

test('the bar shows Latency with the contributors', async ({ page }) => {
  await openApp(page, { reset: false });
  const report = await page.evaluate(async () => {
    const { latencyReport } = await import('/lib/chain-latency.mjs');
    return latencyReport({ base: .01, output: .02, blocks: [{ id: 'lim', title: 'Limiter', seconds: .005 }] });
  });
  expect(report.label).toBe('Latency 35 ms');
  expect(report.tooltip).toContain('Limiter 5.0 ms');
});
