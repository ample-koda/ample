import { test, expect } from '@playwright/test';
import { readdirSync, readFileSync } from 'node:fs';
import { freshServer, openApp } from '../../web/tests/support';
// @ts-ignore - plain ES module shared with the browser and the node tests
import { probesFor } from '../../web/lib/live-probes.mjs';
// @ts-ignore
import { LIVE_PARAMETERS } from '../../web/lib/live-parameters.mjs';

// Plan local-preview 1.1: for every block parameter the live table calls 'param', moving the knob in the middle of a render changes what comes out (and leaves the first half alone).
// The values come from the block template (default and a far value); web/lib/live-probes.mjs lists the few parameters that need a different probe or are covered elsewhere.
freshServer();
const dir = new URL('../../spec/templates/', import.meta.url);
const parametersOf = (type: string) => JSON.parse(readFileSync(new URL(`${type}.json`, dir), 'utf8')).parameters as any[];
const jobs = Object.keys(LIVE_PARAMETERS).flatMap((type) => probesFor(type, parametersOf(type)).map((probe: any) => ({ type, defaults: Object.fromEntries(parametersOf(type).map((p) => [p.name, p.default])), ...probe })));

test('@audio the table of live parameters is complete', () => {
  expect(readdirSync(dir).length).toBeGreaterThan(50);
  for (const job of jobs) expect(job.skip ? job.skip.length : job.b, `${job.type}.${job.param}`).toBeTruthy();
});

test('@audio a knob move changes the sound of every live parameter', async ({ page }) => {
  await openApp(page, { reset: false });
  const rows = await page.evaluate(async (jobs) => {
    const { liveEffectGraph, prepareWorklets } = await import('/app/studio/preview/audio.js');
    const { testSignal } = await import('/app/live-parity.js');
    const rate = 44100, seconds = 2, frames = rate * seconds, signal = testSignal(seconds, rate);
    const render = async (job: any, move: boolean) => {
      const context = new OfflineAudioContext(2, frames, rate), buffer = context.createBuffer(2, frames, rate);
      signal.channels.forEach((channel: Float32Array, c: number) => buffer.copyToChannel(channel, c));
      await prepareWorklets(context);
      const source = context.createBufferSource(), out = context.createGain(); source.buffer = buffer; out.connect(context.destination);
      const first = { ...job.defaults, ...job.base, [job.param]: job.a }, second = { ...first, [job.param]: job.b };
      const impulse = context.createBuffer(2, rate, rate); impulse.copyToChannel(Float32Array.from({ length: rate }, (_, i) => Math.sin(i * 12.9898) * Math.exp(-i / 4000)), 0); impulse.copyToChannel(Float32Array.from({ length: rate }, (_, i) => Math.sin(i * 78.233) * Math.exp(-i / 4000)), 1);
      const graph = liveEffectGraph(context, source, out, job.type, first, 120, job.type === 'convolution' ? impulse : null);
      if (move) context.suspend(1).then(async () => { graph.update(second); await new Promise((r) => setTimeout(r, 80)); context.resume(); });   // the pause lets a message to a worklet arrive before the render goes on
      source.start();
      const result = await context.startRendering(); graph.dispose();
      return [result.getChannelData(0), result.getChannelData(1)];
    };
    const out: any[] = [];
    for (const job of jobs.filter((j: any) => !j.skip)) {
      const still = await render(job, false), moved = await render(job, true);
      let head = 0, tail = 0, energy = 0;
      for (let c = 0; c < 2; c++) for (let i = 0; i < frames; i++) {
        const d = moved[c][i] - still[c][i];
        if (i < rate * .95) head += d * d; else if (i > rate * 1.01) { tail += d * d; energy += still[c][i] * still[c][i]; }
      }
      out.push({ what: `${job.type}.${job.param} ${job.a} -> ${job.b}`, headDb: 10 * Math.log10(Math.max(head, 1e-20)), tailDb: 10 * Math.log10(Math.max(tail, 1e-20) / Math.max(energy, 1e-20)) });
    }
    return out;
  }, jobs);
  const quiet: string[] = [];
  for (const row of rows) {
    expect.soft(row.headDb, `${row.what}: nothing may change before the knob moves`).toBeLessThan(-60);
    if (row.tailDb < -50) quiet.push(`${row.what} (${row.tailDb.toFixed(1)} dB)`);
  }
  expect.soft(quiet, 'parameters whose knob move changed the sound by less than 50 dB below it').toEqual([]);
});
