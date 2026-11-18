import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// A new project or a new song in Remix is a starter song that plays at once: chords, a bass and a beat (a kick and a hat) through a mixer to the master,
// with built-in synths only, so it sounds in an empty project.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/#token=');

async function fresh(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
}

/** The level of the sound that was built: the peak, the loudness (RMS) and how long it is, from the sound the Output pane holds. */
const heard = (page: Page) => page.evaluate(async () => {
  const blob = (document.querySelector('#lite-output') as HTMLAudioElement & { ampleBlob?: Blob }).ampleBlob!;
  const context = new AudioContext();
  const buffer = await context.decodeAudioData(await blob.arrayBuffer());
  const data = buffer.getChannelData(0);
  let peak = 0, sum = 0;
  for (let i = 0; i < data.length; i++) { const v = Math.abs(data[i]); if (v > peak) peak = v; sum += v * v; }
  // the loudness of every bar (the song has four): none of them is silent
  const bar = Math.floor(buffer.sampleRate * 60 / 100 * 4), bars: number[] = [];
  for (let b = 0; b < 4; b++) { let s = 0; for (let i = b * bar; i < Math.min((b + 1) * bar, data.length); i++) s += data[i] * data[i]; bars.push(Math.sqrt(s / bar)); }
  return { peak, rms: Math.sqrt(sum / data.length), seconds: buffer.duration, bars };
});

test('a new project starts with a song that has chords, a bass, a beat, a mixer and a master, and it plays', async ({ page }) => {
  await fresh(page);
  await page.click('#lite-new-project');
  await page.fill('#lite-dialog-field', 'My first song');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-filename')).toHaveText('song.amplet');
  const editor = page.locator('#lite-editor .cm-content');
  await expect(editor).toContainText('composition "My first song"');
  await expect(editor).toContainText('chords Am F C G');
  await editor.click();
  await page.keyboard.press('Control+End');   // the editor draws the lines that are in view: the end of the song has the mixer and the master
  await expect(editor).toContainText('mixer console');
  await expect(editor).toContainText('master out');
  await expect(page.locator('#lite-problem-count')).toHaveText('none');
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 90000 });
  const level = await heard(page);
  expect(level.seconds).toBeGreaterThan(9);
  expect(level.seconds).toBeLessThan(11.5);
  expect(level.peak, 'it makes sound').toBeGreaterThan(0.1);
  expect(level.peak, 'and does not clip').toBeLessThan(1);
  expect(level.rms).toBeGreaterThan(0.02);
  for (const bar of level.bars) expect(bar, 'every bar of the song sounds').toBeGreaterThan(0.01);
});

test('a new song file in an open project is the starter song too, and Listen plays it', async ({ page }) => {
  await fresh(page);
  await page.click('#lite-demos-btn');
  await page.locator('#lite-demo-list li[data-demo="hello-melody"] button').click();
  await expect(page.locator('#lite-demo-dialog')).toBeHidden();
  await page.click('#lite-new-file');
  await page.fill('#lite-dialog-field', 'second.amplet');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-filename')).toHaveText('second.amplet');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('A starter song');
  await page.click('#lite-listen-play');
  await expect(page.locator('#lite-listen-status')).toContainText('hearing build #1', { timeout: 90000 });
  await page.click('#lite-listen-play');
});
