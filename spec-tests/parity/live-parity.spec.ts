import { test, expect } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();

// The measured distance between the live graph and the browser render of each block (default parameters, 2 s test sound). The exact ones are the blocks that run a shared core in a worklet.
test('live against render: the worklet blocks are exact and every other live block has a measured deviation', async ({ page }) => {
  await openApp(page);
  const table = await page.evaluate(async () => {
    const { parityCheck, PARITY_REFERENCES } = await import('/app/live-parity.js');
    const params: Record<string, Record<string, string>> = {
      reverb: { size: '50', decay: '60', mix: '40' }, gate: { threshold: '-30 dB' }, limiter: { ceiling: '-6 dB' }, deesser: { threshold: '-40 dB' }, eq: { low: '4 dB', high: '-3 dB' },
      peq: { band2_gain: '4 dB' }, filter: { cutoff: '800 Hz' }, compressor: { threshold: '-18 dB', ratio: '4' }, delay: { time: '250 ms', feedback: '35', mix: '30' }, distortion: { drive: '12 dB', mix: '100' },
      chorus: { depth: '5', period: '1500 ms', mix: '50' }, flanger: { depth: '2 ms', period: '2000 ms', feedback: '50', mix: '50' }, phaser: { stages: '4', period: '2500 ms', mix: '50' },
      pan: { pan: '16' }, width: { width: '150' }, volume: { gain: '-6 dB' }, tremolo: { shape: 'sine', rate: '4', depth: '50' },
    };
    const out: Record<string, { db: number; word: string }> = {};
    for (const type of Object.keys(PARITY_REFERENCES)) {
      const result = await parityCheck(type, params[type] || {}, { tempo: 120 });
      out[type] = { db: Math.round(result.deviationDb * 10) / 10, word: result.word };
    }
    return out;
  });
  console.log('live vs render (dB of difference below the render):', JSON.stringify(table));
  for (const type of ['reverb', 'gate', 'limiter', 'deesser']) expect(table[type].word, type).toBe('exact');
  for (const entry of Object.values(table)) expect(Number.isFinite(entry.db)).toBe(true);
});
