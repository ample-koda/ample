import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The live preview of an audio component: a Live switch next to the knobs, the sound that feeds the block loops through a real-time chain, a moving knob only
// sends its value to the server (graph-values) and writes nothing until it is released.
function wav(rate = 44100): Buffer {
  const samples = [...Array(rate)].map((_, i) => Math.round(8000 * Math.sin((2 * Math.PI * 220 * i) / rate)));
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}

const LIVE = '/** A delay you can hear while you turn it. */\ncomponent koda.fx.liveEcho version 1.0.0 current {\n  id "demo.koda.fx.live-echo"\n  input audio: Audio\n  #[MinValue(0%), MaxValue(90%)]\n  property feedback: Percent = 30%\n  property delayTime: Duration = 250ms\n  output audio: Audio\n  ui {\n    knob feedback\n    meter audio\n    preview live\n  }\n  process {\n    emit audio\n      |> delay(time=delayTime, feedback=feedback)\n  }\n}\n';
const LIMITER = 'component koda.fx.noLive version 1.0.0 current {\n  id "demo.koda.fx.no-live"\n  input audio: Audio\n  property amount: Percent = 30%\n  output audio: Audio\n  ui {\n    knob amount\n    preview live\n  }\n  process {\n    emit audio\n      |> limiter()\n  }\n}\n';

freshServer({
  song: 'composition "live" { ample 1  tempo 100  master-seed 3 }\nimport "components/live-echo.amplec"\nimport "components/no-live.amplec"\nimport-audio src at 1 for 1 {\n  file samples/a.wav\n}\nkoda.fx.liveEcho echo {\n  from src.audio -> audio\n}\nkoda.fx.noLive plain {\n  from src.audio -> audio\n}\n',
  prepare: (_cli, folder) => {
    fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
    fs.mkdirSync(path.join(folder, 'samples'), { recursive: true });
    fs.writeFileSync(path.join(folder, 'components', 'live-echo.amplec'), LIVE);
    fs.writeFileSync(path.join(folder, 'components', 'no-live.amplec'), LIMITER);
    fs.writeFileSync(path.join(folder, 'samples', 'a.wav'), wav());
  },
});

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('a component that cannot run in real time has a Live switch that is off, with the reason', async ({ page }) => {
  await node(page, 'plain').click({ position: { x: 24, y: 12 } });
  const live = page.locator('#props .cui-live');
  await expect(live).toBeDisabled();
  await expect(live).toHaveAttribute('title', /limiter/);
});

test('the Live switch loops the sound that feeds the block, a moving knob sends values and writes nothing, and the switch stops it', async ({ page }) => {
  const take = await page.evaluate(async () => { const store = await import('/app/store.js'); await store.build(); return store.S.build?.takes?.src || ''; });
  expect(take).toMatch(/^sha256/);
  let sent = 0;
  await page.route('**/api/components/graph-values', (route) => { sent++; return route.continue(); });
  await node(page, 'echo').click({ position: { x: 24, y: 12 } });
  const live = page.locator('#props .cui-live');
  await expect(live).toBeEnabled();
  await live.click();
  await expect(page.locator('#props .cui-live')).toHaveAttribute('aria-pressed', 'true', { timeout: 15000 });
  expect(await page.evaluate(async () => (await import('/app/player.js')).playerState().take)).toBe('audition');
  const before = (await api('/api/doc?path=song.amplet')).text;
  const knob = page.locator('#props .cui svg[role=slider]').first();
  const box = (await knob.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2 - 30, { steps: 6 });
  await expect.poll(() => sent, { timeout: 10000 }).toBeGreaterThan(0);   // the values reached the server while the button was still down
  expect((await api('/api/doc?path=song.amplet')).text).toBe(before);   // and nothing was written yet
  await page.mouse.up();
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toMatch(/feedback \d+%/);   // released: one write
  await page.locator('#props .cui-live').click();
  await expect(page.locator('#props .cui-live')).toHaveAttribute('aria-pressed', 'false', { timeout: 10000 });
  expect(await page.evaluate(async () => (await import('/app/player.js')).playerState().playing)).toBe(false);
});

test('without a built source the switch says what to build first and starts nothing', async ({ page }) => {
  // the tests of this file share one server: an earlier test may have built the source already, and then there is nothing to say
  test.skip((await node(page, 'src').locator('.player').count()) > 0, 'the source was built by an earlier test');
  await node(page, 'echo').click({ position: { x: 24, y: 12 } });
  await page.locator('#props .cui-live').click();
  await expect(page.locator('#toast')).toContainText('Build src first');
  await expect(page.locator('#props .cui-live')).toHaveAttribute('aria-pressed', 'false');
});
