import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import { freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();

// Local preview 6.5: the live graphs (Web Audio nodes and worklets) against the vectors the real server engine made (web/lib/dsp-vectors.json, tools/gen-dsp-vectors.mjs).
// The limits are the measured distances plus about 3 dB: a block may not get worse; when one gets better, lower its limit.
const LIVE_LIMITS_DB: Record<string, number> = {
  reverb: -75, gate: -60, limiter: -25, deesser: -50, eq: -15, peq: -3, filter: 8, compressor: -28, delay: -54, distortion: -70, chorus: -8, flanger: 4, phaser: -7, pan: -68, width: -58, volume: -49, tremolo: -56,
};
const vectors = JSON.parse(fs.readFileSync(new URL('../../web/lib/dsp-vectors.json', import.meta.url), 'utf8'));

test('@audio live graphs against the server vectors', async ({ page }) => {
  await openApp(page);
  const table = await page.evaluate(async (cases) => {
    const { renderLive, testSignal } = await import('/app/live-parity.js');
    const raw = testSignal(0.4);
    const input = { sampleRate: raw.sampleRate, channels: raw.channels.map((c: Float32Array) => Float32Array.from(c, (v) => Math.max(-32768, Math.min(32767, Math.round(v * 32768))) / 32768)) };
    const out: Record<string, number> = {};
    for (const [type, entry] of Object.entries<any>(cases)) {
      const live = await renderLive(type, entry.params, input, entry.tempo);
      let difference = 0, energy = 0;
      const first = Math.floor(input.sampleRate * 0.05);
      entry.channels.forEach((text: string, c: number) => {
        const bytes = Uint8Array.from(atob(text), (ch) => ch.charCodeAt(0)), ints = new Int16Array(bytes.buffer);
        const candidate = live.channels[Math.min(c, live.channels.length - 1)];
        for (let i = first; i < ints.length - live.latencySamples; i++) { const s = ints[i] / 32768, d = candidate[i + live.latencySamples] - s; difference += d * d; energy += s * s; }
      });
      out[type] = energy > 0 ? 10 * Math.log10(Math.max(difference, 1e-20) / energy) : NaN;
    }
    return out;
  }, vectors.cases);
  console.log('live vs server (dB below the server):', JSON.stringify(Object.fromEntries(Object.entries(table).map(([k, v]) => [k, Math.round(v * 10) / 10]))));
  // AMPLE_WRITE_CONFORMANCE=1 stores the measurement for tools/dsp-deviations.mjs (the table in docs/clientside-preview/conformance.md)
  if (process.env.AMPLE_WRITE_CONFORMANCE) fs.writeFileSync(new URL('../../web/lib/dsp-live-measured.json', import.meta.url), JSON.stringify(Object.fromEntries(Object.entries(table).map(([k, v]) => [k, Math.round(v * 10) / 10])), null, 1));
  for (const [type, db] of Object.entries(table)) {
    expect(Number.isFinite(db), type).toBe(true);
    if (type in LIVE_LIMITS_DB) expect(db, type).toBeLessThanOrEqual(LIVE_LIMITS_DB[type]);
  }
});
