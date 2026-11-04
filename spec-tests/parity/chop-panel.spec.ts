import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The chop window shows the recording with its slices; the pattern field and a click on the wave write the block, Play works the chopped sound out in the browser, and the song preview needs no server build for it.
const SONG = `composition "Local chop" { ample 1  tempo 120  master-seed 17 }
import-audio source at 1 for 2 {
  file source.wav
}
chop words {
  slices auto
  pattern "1 2 3"
  from source.audio -> audio
}
master master {
  from words.audio -> audio
}
`;

// two seconds with three tone bursts (0.1-0.4 s, 0.7-1.0 s, 1.3-1.6 s)
function sourceWav() {
  const rate = 44100, data = Buffer.alloc(rate * 2 * 2), wav = Buffer.alloc(44 + data.length);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVE', 8); wav.write('fmt ', 12);
  wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(rate, 24);
  wav.writeUInt32LE(rate * 2, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(data.length, 40);
  for (let frame = 0; frame < rate * 2; frame++) {
    const t = frame / rate, on = (t >= 0.1 && t < 0.4) || (t >= 0.7 && t < 1.0) || (t >= 1.3 && t < 1.6);
    data.writeInt16LE(on ? Math.round(Math.sin(2 * Math.PI * 300 * t) * 9000) : 0, frame * 2);
  }
  data.copy(wav, 44);
  return wav;
}

freshServer({ song: SONG, prepare: (_cli, folder) => fs.writeFileSync(path.join(folder, 'source.wav'), sourceWav()) });

const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('the window shows the slices and the pattern, and a pattern is written into the block', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'words').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const panel = page.locator('#studio[open] .studio-chop-panel');
  await expect(panel).toBeVisible();
  await expect(panel.locator('.chop-info')).toContainText('3 slices', { timeout: 20000 });
  await expect(panel.locator('.chop-step')).toHaveCount(3);
  await expect(panel.locator('.studio-panel-control')).toHaveCount(6);
  const pattern = panel.locator('.chop-pattern');
  await pattern.fill('1 2 3 3*2 -');
  await pattern.press('Enter');
  await expect.poll(docText, { timeout: 8000 }).toMatch(/pattern "?1 2 3 3\*2 -"?/);
  await expect(panel.locator('.chop-step')).toHaveCount(5);
  // a pattern that names a slice that is not there is refused with a message
  await pattern.fill('1 9');
  await pattern.press('Enter');
  await expect(panel.locator('.chop-info')).toContainText('not a slice number');
  // a click on the wave adds a cut: the slices become a list of times
  const box = (await panel.locator('canvas.chop-wave').boundingBox())!;
  await page.mouse.click(box.x + box.width * 0.5, box.y + box.height / 2);
  await expect.poll(docText, { timeout: 8000 }).toMatch(/slices "?[\d. ]+"?/);
  await expect(panel.locator('.chop-info')).toContainText('4 slices');
});

test('Play works the chopped sound out in the browser', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'words').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const play = page.locator('#studio[open] .chop-play'), status = page.locator('#studio[open] .vocoder-status');
  await expect(page.locator('#studio[open] .chop-info')).toContainText('slices', { timeout: 20000 });
  // the window is built again when the takes of the build reach the page: click again while it has not started
  await expect.poll(async () => { if ((await play.getAttribute('aria-pressed')) === 'true') return true; if (/^(Press Play|Build the)/.test(await status.innerText())) await play.click(); return false; }, { timeout: 30000, intervals: [500] }).toBe(true);
  await expect(status).toContainText('a knob is heard while it plays');
  const gate = page.locator('#studio[open] .studio-panel-control', { hasText: 'Gate' }).getByRole('slider');
  await gate.focus();
  await page.keyboard.press('ArrowDown');
  await expect(play).toHaveAttribute('aria-pressed', 'true', { timeout: 10000 });
  await play.click();
  await expect(play).toHaveAttribute('aria-pressed', 'false');
});

test('the chop is processed in the client chain without a server build', async ({ page }) => {
  await openApp(page, { reset: false });
  const builds: string[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(String(request.postData())); });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 30000 }).toBe(true);
  expect(builds.filter((b) => b.includes('words'))).toHaveLength(0);
  await page.click('#btn-stop');
});
