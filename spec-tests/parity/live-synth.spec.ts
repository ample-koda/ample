import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 3.3: the live synth worklet plays the notes with the voice of the render (synth-core.mjs): sample-equal.
freshServer();
test('the synth worklet gives the samples of the render core', async ({ page }) => {
  await openApp(page, { reset: false });
  const r = await page.evaluate(async () => {
    const core = await import('/lib/synth-core.mjs');
    const rate = 48000, frames = rate * 2, params = { wave: 'saw', detune: '10', cutoff: '4000 Hz', attack: '5 ms', release: '120 ms' };
    const list = [{ start: 4800, pitch: 60, velocity: 100, held: 24000, pan: 0, fine: 0 }, { start: 30000, pitch: 67, velocity: 80, held: 9600, pan: -40, fine: 20 }, { start: 31000, pitch: 72, velocity: 127, held: 12000, pan: 60, fine: 0 }];
    // the render core, note by note
    const left = new Float32Array(frames), right = new Float32Array(frames), settings = core.synthSettings(params, rate);
    for (const n of list) { const v = new core.SynthVoice(settings, n); v.process(left, right, n.start, Math.min(frames, n.start + v.length)); }
    // the worklet
    const context = new OfflineAudioContext(2, frames, rate); await context.audioWorklet.addModule('/lib/synth-worklet.mjs');
    const node = new AudioWorkletNode(context, 'ample-synth', { numberOfInputs: 0, outputChannelCount: [2] }); node.connect(context.destination);
    node.port.postMessage({ type: 'settings', params });
    for (const n of list) node.port.postMessage({ type: 'on', at: n.start / rate, pitch: n.pitch, velocity: n.velocity, duration: n.held / rate, pan: n.pan, fine: n.fine });
    context.suspend(0).then(async () => { await new Promise((resolve) => setTimeout(resolve, 150)); context.resume(); });
    const out = await context.startRendering();
    let worst = 0, energy = 0; const l = out.getChannelData(0), rr = out.getChannelData(1);
    for (let i = 0; i < frames; i++) { worst = Math.max(worst, Math.abs(l[i] - left[i]), Math.abs(rr[i] - right[i])); energy += left[i] * left[i]; }
    return { worst, energy };
  });
  expect(r.energy).toBeGreaterThan(1);
  expect(r.worst).toBeLessThan(1e-6);   // float32 output of a float64 core
});

test('a worklet with more than sixteen notes steals the oldest', async ({ page }) => {
  await openApp(page, { reset: false });
  const r = await page.evaluate(async () => {
    const rate = 48000, frames = rate;
    const context = new OfflineAudioContext(2, frames, rate); await context.audioWorklet.addModule('/lib/synth-worklet.mjs');
    const node = new AudioWorkletNode(context, 'ample-synth', { numberOfInputs: 0, outputChannelCount: [2] }); node.connect(context.destination);
    node.port.postMessage({ type: 'settings', params: { wave: 'sine', attack: '1 ms', release: '50 ms' } });
    for (let k = 0; k < 20; k++) node.port.postMessage({ type: 'on', at: 0, pitch: 40 + k, velocity: 100, duration: .5, pan: 0, fine: 0 });
    context.suspend(0).then(async () => { await new Promise((resolve) => setTimeout(resolve, 150)); context.resume(); });
    const out = await context.startRendering(); const l = out.getChannelData(0); let peak = 0; for (let i = 0; i < frames; i++) peak = Math.max(peak, Math.abs(l[i]));
    return peak;
  });
  expect(r).toBeGreaterThan(0.01);
});
