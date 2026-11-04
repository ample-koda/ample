import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The live Delay Llama: the AudioWorklet loads in the browser, a note sung at a time on the clock of the audio context sounds, and it is silent after the note.
freshServer({ song: 'composition "demo" { ample 1  tempo 120  master-seed 1 }\n' });

test('the live llama sings a note from the worklet and falls silent after it', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.mouse.click(300, 300);   // a gesture, so the audio context may run
  const result = await page.evaluate(async () => {
    const { llamaLive } = await import('/app/llama-live.js');
    const ctx = new AudioContext();
    await ctx.resume();
    const voice = await llamaLive(ctx);
    const analyser = ctx.createAnalyser();
    analyser.fftSize = 2048;
    voice.connect(analyser);
    voice.set({ vibrato: 0, releaseMs: 60 });
    const peak = () => { const data = new Float32Array(analyser.fftSize); analyser.getFloatTimeDomainData(data); return data.reduce((m, v) => Math.max(m, Math.abs(v)), 0); };
    const before = peak();
    voice.noteOn(57, 100, ctx.currentTime + 0.1);
    voice.noteOff(57, ctx.currentTime + 0.9);
    let during = 0;
    for (let i = 0; i < 12; i++) { await new Promise((r) => setTimeout(r, 60)); during = Math.max(during, peak()); }
    await new Promise((r) => setTimeout(r, 900));
    const after = peak();
    await ctx.close();
    return { state: 'ran', before, during, after };
  });
  expect(result.before).toBeLessThan(0.001);
  expect(result.during).toBeGreaterThan(0.02);
  expect(result.after).toBeLessThan(0.01);
});
