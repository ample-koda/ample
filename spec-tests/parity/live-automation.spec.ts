import { test, expect } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();

// Local preview 5.4: a curve followed by the live graph, rendered offline. The ticker of the automation preview hands the value of the curve to graph.update() every 30 ms; here the same
// stepping runs on an OfflineAudioContext (suspend/resume) and the gain that comes out is compared with the curve.
test('@audio a gain ramp followed in 30 ms steps renders the curve within 0.25 dB, and the ramp restarts when the loop wraps', async ({ page }) => {
  await openApp(page, { reset: false });
  const result = await page.evaluate(async () => {
    const { liveEffectGraph } = await import('/app/studio/preview/audio.js');
    const { automationValueAt } = await import('/lib/automation-model.mjs');
    const rate = 48000, seconds = 8, loop = 4, tempo = 120;
    const context = new OfflineAudioContext(1, rate * seconds, rate);
    const buffer = context.createBuffer(1, rate * seconds, rate); buffer.getChannelData(0).fill(.5);
    const source = context.createBufferSource(); source.buffer = buffer;
    const points = [{ bar: 1, value: -20 }, { bar: 3, value: 0 }];   // two bars of 2 s at 120 bpm: a ramp over 4 s
    const range = { min: -60, max: 48 };
    const graph = liveEffectGraph(context, source, context.destination, 'volume', { gain: '-20' }, tempo);
    source.start();
    const valueAt = (time: number) => automationValueAt(points, 1 + (time % loop) / 2, range) as number;
    for (let time = .03; time < seconds; time += .03) {
      context.suspend(time).then(() => { graph.update({ gain: String(valueAt(time)) }); context.resume(); });
    }
    const out = (await context.startRendering()).getChannelData(0);
    const db = (time: number) => 20 * Math.log10(Math.abs(out[Math.round(time * rate)]) / .5);
    return [.5, 1, 2, 3, 3.9, 4.5, 6, 7.5].map((time) => ({ time, got: db(time), want: valueAt(time) }));
  });
  for (const { time, got, want } of result) expect(Math.abs(got - want), `t=${time}`).toBeLessThan(0.25);
  expect(result.find((r) => r.time === 3.9)!.got).toBeGreaterThan(-1.5);   // end of the first ramp
  expect(result.find((r) => r.time === 4.5)!.got).toBeLessThan(-15);      // wrapped: the ramp starts again
});

test('@audio Live follows more parameters now, and the sentence about them comes from the list', async ({ page }) => {
  await openApp(page, { reset: false });
  const info = await page.evaluate(async () => { const m = await import('/lib/automation-model.mjs'); return { pan: m.liveSupported('pan', 'pan'), flanger: m.liveSupported('flanger', 'mix'), bit: m.liveSupported('bitcrusher', 'mix'), text: m.liveSupportedText() }; });
  expect(info.pan && info.flanger).toBe(true);
  expect(info.bit).toBe(false);
  expect(info.text).toContain('filter cutoff, filter resonance');
});
