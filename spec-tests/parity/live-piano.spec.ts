import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 3.4: the live felt piano worklet gives the samples of the render core.
freshServer();
test('the piano worklet gives the samples of the render core', async ({ page }) => {
  await openApp(page, { reset: false });
  const r = await page.evaluate(async () => {
    const core = await import('/lib/piano-core.mjs');
    const rate = 48000, frames = rate * 2, list = [{ start: 4800, pitch: 60, length: 24000 }, { start: 12000, pitch: 64, length: 30000 }, { start: 12000, pitch: 67, length: 30000 }];
    const mono = new Float32Array(frames);
    for (const n of list) { const v = new core.PianoVoice(rate, n); v.process(mono, n.start, Math.min(frames, n.start + v.length)); }
    const context = new OfflineAudioContext(2, frames, rate); await context.audioWorklet.addModule('/lib/piano-worklet.mjs');
    const node = new AudioWorkletNode(context, 'ample-piano', { numberOfInputs: 0, outputChannelCount: [2] }); node.connect(context.destination);
    for (const n of list) node.port.postMessage({ type: 'on', at: n.start / rate, pitch: n.pitch, duration: n.length / rate });
    context.suspend(0).then(async () => { await new Promise((resolve) => setTimeout(resolve, 150)); context.resume(); });
    const out = await context.startRendering(); const l = out.getChannelData(0), rr = out.getChannelData(1);
    let worst = 0, energy = 0; for (let i = 0; i < frames; i++) { worst = Math.max(worst, Math.abs(l[i] - mono[i]), Math.abs(rr[i] - mono[i])); energy += mono[i] * mono[i]; }
    return { worst, energy };
  });
  expect(r.energy).toBeGreaterThan(.1);
  expect(r.worst).toBeLessThan(1e-6);
});
