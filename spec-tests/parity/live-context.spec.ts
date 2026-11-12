import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 1.2: the page has one AudioContext, shared by the audition, the Jam and the player's meters.
freshServer();
test('every live caller gets the same AudioContext and it runs after a gesture', async ({ page }) => {
  await openApp(page, { reset: false });
  const result = await page.evaluate(async () => {
    const engine = await import('/lib/audio-engine.mjs');
    const live = await import('/app/live-play.js');
    const a = engine.getContext(), b = live.liveAudioContext();
    engine.resumeOnGesture();
    await new Promise((r) => setTimeout(r, 200));
    return { same: a === b, rate: engine.sampleRate() === a.sampleRate, state: a.state, latency: typeof engine.outputLatency().total, info: live.audioLatencyInfo().state };
  });
  expect(result.same).toBe(true);
  expect(result.rate).toBe(true);
  expect(result.latency).toBe('number');
  expect(['running', 'suspended']).toContain(result.state);
});
