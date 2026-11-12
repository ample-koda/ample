import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 2.2: the delay parameter `sync` locks the echo to the tempo in the live graph, the offline JS port and (by the engine test DelaySyncTests) the build.
freshServer();
test('the live and the offline delay put the echo at the note value of the tempo', async ({ page }) => {
  await openApp(page, { reset: false });
  const r = await page.evaluate(async () => {
    const { liveEffectGraph } = await import('/app/studio/preview/audio.js');
    const { delayAudio } = await import('/lib/clientside-delay.mjs');
    const rate = 44100, frames = rate * 2, click = new Float32Array(frames); click[0] = .8;
    const params = { time: '250 ms', sync: '1/8', feedback: '0%', mix: '100%', enabled: 'on' };
    const context = new OfflineAudioContext(1, frames, rate), buffer = context.createBuffer(1, frames, rate); buffer.copyToChannel(click, 0);
    const source = context.createBufferSource(), out = context.createGain(); source.buffer = buffer; out.connect(context.destination);
    liveEffectGraph(context, source, out, 'delay', params, 90, null); source.start();
    const live = (await context.startRendering()).getChannelData(0);
    const offline = (await delayAudio({ sampleRate: rate, channels: [click] }, params, undefined, 90)).channels[0];
    const peak = (data: Float32Array) => { let best = 0, at = 0; for (let i = 100; i < data.length; i++) if (Math.abs(data[i]) > best) { best = Math.abs(data[i]); at = i; } return at; };
    return { live: peak(live), offline: peak(offline), expected: Math.round(Math.floor(4 * 60 * rate * 1000 / 90000) / 8) };
  });
  expect(r.offline).toBe(r.expected);
  expect(Math.abs(r.live - r.expected)).toBeLessThanOrEqual(128);   // the live loop delay carries up to one render quantum
});
