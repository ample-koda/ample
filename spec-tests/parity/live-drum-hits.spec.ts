import { test, expect } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';
import { startedChain, wavFixture } from './support/live-chain-fixture';

freshServerPerTest();

// Follow-up F2: the built-in drums of the live chain are single hits triggered on the audio clock, not a rendered loop that is swapped when a knob moves.
test('@audio the drum voice starts every hit at exactly its time on the audio clock, across a loop wrap', async ({ page }) => {
  await openApp(page, { reset: false });
  const r = await page.evaluate(async () => {
    const { createDrumVoice } = await import('/app/live-drums.js');
    const starts: number[] = [];
    let now = 100;
    const context: any = { get currentTime() { return now; }, sampleRate: 44100, createBuffer: (c: number, n: number, rate: number) => ({ duration: n / rate, copyToChannel() {} }), createBufferSource: () => ({ connect() {}, stop() {}, start(when: number) { starts.push(when); }, buffer: null }) };
    const t0 = 100.08, loop = 2, hit = (at: number) => ({ at, kind: 'hat', samples: new Float32Array(441) });
    const voice = createDrumVoice(context, {}, { span: () => ({ start: 0, length: loop }), positionAt: (time: number) => (((time - t0) % loop) + loop) % loop });
    voice.set({ hits: [hit(0), hit(.5), hit(1.75)] });
    voice.start(t0);
    for (let step = 0; step < 90; step++) { now = 100 + step * .05; await new Promise((resolve) => setTimeout(resolve, 30)); }
    voice.stop();
    return { starts, t0, loop };
  });
  const expected = [0, .5, 1.75, 2, 2.5, 3.75].map((at) => r.t0 + at);
  const seen = [...new Set(r.starts.map((value) => Math.round(value * 1e6) / 1e6))].filter((value) => value <= r.t0 + 3.75 + 1e-6);
  expect(seen.slice(0, expected.length)).toEqual(expected.map((value) => Math.round(value * 1e6) / 1e6));
  expect(r.starts.length, 'no hit is started twice').toBe(new Set(r.starts.map((v) => Math.round(v * 1e6))).size);
});

test('@audio a pattern knob of built-in drums in the chain changes the hits at once, without a render or a swap', async ({ page }) => {
  await startedChain(page, [['volume', 'vol']], wavFixture(), async () => {
    await page.evaluate(() => import('/app/store.js').then((s) => s.edit([{ type: 'setParameter', block: 'src', name: 'kit', value: 'builtin' }])));
  });
  const hits = () => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainDrumHits('src')));
  const a = await hits();
  expect(a, 'the drums play as hits').toBeGreaterThan(20);
  const started = Date.now();
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainPreview('src', 'pattern', 'beat-B')));
  await expect.poll(hits, { timeout: 1000 }).toBeGreaterThan(a);   // beat-B has two more kicks a bar
  expect(Date.now() - started, 'well under the 150 ms of a re-render and swap, plus the poll').toBeLessThan(400);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.swaps, 'no swap of a rendered loop').toBe(0);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
