import { test, expect } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { freshServer, openApp, ownServer } from '../../web/tests/support';
import { startedChain, wavFixture } from './support/live-chain-fixture';

// Plan local-preview 2.3: changing the impulse length or normalize of a playing convolution reverb crossfades between two convolvers instead of swapping the buffer, so there is no click.
freshServer();
test('@audio a length change mid-render has no jump, and the tail follows the new length', async ({ page }) => {
  await openApp(page, { reset: false });
  const r = await page.evaluate(async () => {
    const { liveEffectGraph } = await import('/app/studio/preview/audio.js');
    const rate = 44100, frames = rate * 3;
    const make = async (change: boolean) => {
      const context = new OfflineAudioContext(2, frames, rate), buffer = context.createBuffer(2, frames, rate);
      // a steady 220 Hz tone (the impulse is a smooth decaying 150 Hz tone, so the output is smooth and a click would stand out) for 1.5 s, then silence: the tail after 1.5 s is the impulse response itself
      const tone = Float32Array.from({ length: frames }, (_, i) => i < rate * 1.5 ? .4 * Math.sin(2 * Math.PI * 220 * i / rate) : 0);
      buffer.copyToChannel(tone, 0); buffer.copyToChannel(tone, 1);
      const impulse = context.createBuffer(2, rate * 4, rate);
      for (let c = 0; c < 2; c++) impulse.copyToChannel(Float32Array.from({ length: rate * 4 }, (_, i) => Math.sin(2 * Math.PI * (c ? 170 : 150) * i / rate) * Math.exp(-i / (rate * .8))), c);
      const source = context.createBufferSource(), out = context.createGain(); source.buffer = buffer; out.connect(context.destination);
      const first = { length: '3', normalize: 'on', predelay: '0', level: '0', mix: '100', enabled: 'on' };
      const graph = liveEffectGraph(context, source, out, 'convolution', first, 120, impulse);
      if (change) context.suspend(1).then(() => { graph.update({ ...first, length: '0.3' }); context.resume(); });
      source.start();
      const result = await context.startRendering(); graph.dispose();
      return result.getChannelData(0);
    };
    const still = await make(false), moved = await make(true);
    const maxStep = (a: Float32Array, from: number, to: number) => { let m = 0; for (let i = from + 1; i < to; i++) m = Math.max(m, Math.abs(a[i] - a[i - 1])); return m; };
    const rms = (a: Float32Array, from: number, to: number) => { let s = 0; for (let i = from; i < to; i++) s += a[i] * a[i]; return Math.sqrt(s / (to - from)); };
    return {
      stepBefore: Math.max(maxStep(moved, rate * .5, rate * .95), maxStep(moved, rate * 1.3, rate * 1.5)), stepAround: maxStep(moved, rate, rate + 4410),
      identicalBefore: maxStep(Float32Array.from(moved.subarray(0, rate * .95), (v, i) => v - still[i]), 0, rate * .95),
      tailStill: rms(still, rate * 2.4, rate * 2.9), tailMoved: rms(moved, rate * 2.4, rate * 2.9),
    };
  });
  expect(r.identicalBefore, 'nothing changes before the knob moves').toBeLessThan(1e-6);
  expect(r.stepAround, 'no click around the swap (a plain buffer swap measured 8 times the steady step)').toBeLessThan(r.stepBefore * 2);
  expect(r.tailMoved, 'a shorter impulse leaves a much shorter tail').toBeLessThan(r.tailStill * .5);
});

// Local preview follow-up: a convolution block can sit in the live chain. Its impulse response is fetched when the chain starts, a changed `ir` loads the new one into the idle convolver and crossfades.
test('@audio a convolution block sits in the live chain, is heard, and a new impulse is loaded without a restart', async ({ page }) => {
  const impulseRequests: string[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/effects/impulse')) impulseRequests.push(request.postData() || ''); });
  await startedChain(page, [['convolution', 'room']], undefined, async () => {
    await writeFile(join(ownServer()!.folder, 'ir-a.wav'), wavFixture(1, 150, 9000)); await writeFile(join(ownServer()!.folder, 'ir-b.wav'), wavFixture(1, 300, 9000));
    await page.evaluate(() => import('/app/store.js').then((s) => s.edit([{ type: 'setParameter', block: 'room', name: 'ir', value: 'file' }, { type: 'setParameter', block: 'room', name: 'file', value: 'ir-a.wav' }])));
  });
  expect(impulseRequests.length, 'the impulse was fetched once when the chain started').toBe(1);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.items.map((item) => item.tpl)).toEqual(['drums', 'convolution']);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => { m.chainPreview('room', 'mix', '100'); m.chainPreview('room', 'predelay', '40'); }));
  await page.evaluate(() => import('/app/store.js').then((s) => s.edit([{ type: 'setParameter', block: 'room', name: 'file', value: 'ir-b.wav' }])));
  await expect.poll(() => impulseRequests.length, { timeout: 8000 }).toBe(2);
  expect(impulseRequests[1]).toContain('ir-b.wav');
  expect(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainActive())), 'the chain kept playing').toBe(true);
  await page.waitForTimeout(800);
  expect(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('room')?.left ?? 0)), 'still sounds after the swap').toBeGreaterThan(0.002);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
