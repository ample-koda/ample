import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 2.3: changing the impulse length or normalize of a playing convolution reverb crossfades between two convolvers instead of swapping the buffer, so there is no click.
freshServer();
test('a length change mid-render has no jump, and the tail follows the new length', async ({ page }) => {
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
