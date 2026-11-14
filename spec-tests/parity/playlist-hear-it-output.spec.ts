import { test, expect, type Page } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Playlist "Hear it" from an idle transport: it plays through the shared output (listening volume), the output level is
// readable for the Mixer meter, and the main Stop ends it.
freshServer();
const wav = () => {
  const rate = 44100, n = rate * 3, buf = Buffer.alloc(44 + n * 2);
  buf.write('RIFF', 0); buf.writeUInt32LE(36 + n * 2, 4); buf.write('WAVEfmt ', 8); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22);
  buf.writeUInt32LE(rate, 24); buf.writeUInt32LE(rate * 2, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34); buf.write('data', 36); buf.writeUInt32LE(n * 2, 40);
  for (let i = 0; i < n; i++) buf.writeInt16LE(Math.round(Math.sin(i * 2 * Math.PI * 220 / rate) * 0.6 * 32767), 44 + i * 2);
  return buf;
};
test('Hear it from idle: volume, output meter and transport Stop', async ({ page }) => {
  await page.route('**/api/takes/t1', (r) => r.fulfill({ json: { audioDataId: 'd1' } }));
  await page.route('**/api/data/d1', (r) => r.fulfill({ body: wav(), contentType: 'audio/wav' }));
  await openApp(page, { reset: false });
  const result = await page.evaluate(async () => {
    const { S } = await import('/app/store.js');
    const { playMix } = await import('/app/playlist-preview.js');
    const { playerState, stop } = await import('/app/player.js');
    const { outputLevels, setPlaybackVolume } = await import('/app/playback-volume.js');
    const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));
    const peak = async () => { let m = 0; for (let i = 0; i < 12; i++) { await wait(25); m = Math.max(m, outputLevels().left); } return m; };
    (S as any).build = { ...(S as any).build, takes: { b1: 't1' } };
    const lines = [{ id: 'a', name: 'a', chain: ['b1'], sources: [] }];
    stop(); setPlaybackVolume(1);
    const idle = playerState().playing;
    const ok = await playMix(lines as any, { firstBar: 1, bars: 1 } as any, 2);
    const playing = playerState().playing;
    const full = await peak();
    setPlaybackVolume(0.25);
    await wait(150);   // the volume glides (a 15 ms time constant): measure after it settled, not across the glide
    const quiet = await peak();
    setPlaybackVolume(1);
    stop();
    const after = playerState().playing;
    await wait(150);
    return { idle, ok, playing, full, quiet, after, silent: outputLevels().left };
  });
  expect(result.idle).toBe(false); expect(result.ok).toBe(true);
  expect(result.playing).toBe(true);
  expect(result.full).toBeGreaterThan(0.1);
  expect(result.quiet).toBeLessThan(result.full * 0.6);
  expect(result.after).toBe(false);
  expect(result.silent).toBeLessThan(0.01);
});

// ---- the real Playlist UI: the Hear it button of the banner, the main Stop of the header, a withheld take download (#103)

/** Opens the Playlist the way a person does, the server reports the test take for every block, and counts the audio sources that start. */
async function openPlaylistWithTakes(page: Page, hold?: Promise<void>) {
  await page.addInitScript(() => {
    localStorage.setItem('ample.playlist.on', '1');
    const w = window as any; w.__sourceStarts = 0;
    const start = AudioBufferSourceNode.prototype.start;
    AudioBufferSourceNode.prototype.start = function (...args: any[]) { w.__sourceStarts++; return (start as any).apply(this, args); };
  });
  await page.route('**/api/takes/t1', (r) => r.fulfill({ json: { audioDataId: 'd1' } }));
  await page.route('**/api/data/d1', async (r) => { if (hold) await hold; await r.fulfill({ body: wav(), contentType: 'audio/wav' }); });
  // the server reports the test take for every block, the way a song that was built before shows as built (no fake state that the next status check undoes)
  await page.route('**/api/build/status?*', async (r) => { const res = await r.fetch(); const body = await res.json(); for (const b of body.blocks || []) { b.takeId = 't1'; b.status = 'cached'; } await r.fulfill({ response: res, json: body }); });
  await openApp(page, { reset: false });
  await page.locator('#btabs button[data-t="playlist"]').click();
  await expect(page.locator('#b-playlist .pl-line').first()).toBeVisible();
  await expect.poll(() => page.evaluate(async () => Object.keys((await import('/app/store.js')).S.build.takes || {}).length)).toBeGreaterThan(2);
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).getByRole('button', { name: 'Mute drums' }).click();
  await expect(page.locator('#b-playlist .pl-banner')).toBeVisible();
}
const hearIt = (page: Page) => page.locator('#b-playlist .pl-banner button', { hasText: 'Hear it' });
const playing = (page: Page) => page.evaluate(async () => (await import('/app/player.js')).playerState().playing);
const starts = (page: Page) => page.evaluate(() => (window as any).__sourceStarts as number);
const outputPeak = (page: Page) => page.evaluate(async () => {
  const { outputLevels } = await import('/app/playback-volume.js');
  let m = 0; for (let i = 0; i < 12; i++) { await new Promise((r) => setTimeout(r, 25)); m = Math.max(m, outputLevels().left); }
  return m;
});

test('Hear it from idle: the main Stop button turns on, ends it, and turns off again', async ({ page }) => {
  await openPlaylistWithTakes(page);
  const stopButton = page.locator('#btn-stop');
  await expect(stopButton).toBeDisabled();
  await hearIt(page).click();
  await expect.poll(() => playing(page)).toBe(true);
  await expect(stopButton).toBeEnabled();
  await expect(stopButton).toHaveAttribute('aria-pressed', 'true');
  expect(await outputPeak(page)).toBeGreaterThan(0.05);
  await stopButton.click();
  await expect.poll(() => playing(page)).toBe(false);
  await expect(stopButton).toBeDisabled();
  await expect(stopButton).toHaveAttribute('aria-pressed', 'false');
  expect(await outputPeak(page)).toBeLessThan(0.01);
});

test('Hear it that ends by itself turns the main Stop button off', async ({ page }) => {
  await openPlaylistWithTakes(page);
  await page.evaluate(async () => { const { S } = await import('/app/store.js'); (S as any).range = { firstBar: 1, bars: 1 }; });
  const stopButton = page.locator('#btn-stop');
  await hearIt(page).click();
  await expect(stopButton).toBeEnabled();
  await expect.poll(() => playing(page), { timeout: 15000 }).toBe(false);   // one bar of the demo song
  await expect(stopButton).toBeDisabled();
});

for (const how of ['Playlist Stop', 'main Stop', 'Clear'] as const) {
  test(`#103: ${how} while the take is still downloading: nothing plays when it arrives`, async ({ page }) => {
    let release!: () => void;
    const hold = new Promise<void>((r) => { release = r; });
    const requested = page.waitForRequest('**/api/data/d1');
    await openPlaylistWithTakes(page, hold);
    await hearIt(page).click();
    await requested;
    if (how === 'main Stop') { await expect(page.locator('#btn-stop')).toBeEnabled(); await page.locator('#btn-stop').click(); }
    else if (how === 'Clear') await page.locator('#b-playlist .pl-banner button', { hasText: 'Clear' }).click();
    else await page.locator('#b-playlist .pl-toolbar button[aria-label="Stop"]').click();
    release();
    await page.waitForTimeout(800);
    expect(await starts(page)).toBe(0);
    expect(await playing(page)).toBe(false);
    await expect(page.locator('#btn-stop')).toBeDisabled();
    expect(await outputPeak(page)).toBeLessThan(0.01);
  });
}

test('#103: a second Hear it while the first is still downloading: only one plays', async ({ page }) => {
  let release!: () => void;
  const hold = new Promise<void>((r) => { release = r; });
  const requested = page.waitForRequest('**/api/data/d1');
  await openPlaylistWithTakes(page, hold);
  await hearIt(page).click();
  await requested;
  await hearIt(page).click();
  await page.waitForTimeout(200);
  release();
  await expect.poll(() => playing(page)).toBe(true);
  await page.waitForTimeout(500);
  expect(await starts(page)).toBe(1);
  await page.locator('#btn-stop').click();
  await expect.poll(() => playing(page)).toBe(false);
  expect(await outputPeak(page)).toBeLessThan(0.01);
});
