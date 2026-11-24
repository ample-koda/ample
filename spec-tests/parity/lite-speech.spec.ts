import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// The Remix flow for speech in a real browser: open the demo, Build, the build gives audio with a length, and a song that is over the
// speech limits says so on the page (AMPL924). The Lite build service is the one of this local server, not a deployed host.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000', AMPLE_LITE_DOWNLOADS_PER_MINUTE: '500', AMPLE_LITE_DOWNLOADS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/#token=');

async function fresh(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
}

async function useDemo(page: Page, id: string) {
  await page.click('#lite-demos-btn');
  await expect(page.locator('#lite-demo-dialog')).toBeVisible();
  await page.locator(`#lite-demo-list li[data-demo="${id}"] button`).click();
  await expect(page.locator('#lite-demo-dialog')).toBeHidden();
  await expect(page.locator('#lite-filename')).not.toHaveText('');
}

test('the Hello speech demo builds in the browser and gives audio', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'speech-hello');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('speech voice');
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 90000 });
  const duration = await page.evaluate(async () => {
    const audio = document.querySelector('#lite-output') as HTMLAudioElement;
    for (let i = 0; i < 50 && !(audio.duration > 0); i++) await new Promise((resolve) => setTimeout(resolve, 200));
    return audio.duration;
  });
  expect(duration).toBeGreaterThan(1);
});

async function buildWith(page: Page, extra: string) {
  await fresh(page);
  await useDemo(page, 'speech-hello');
  await page.locator('#lite-editor .cm-content').click();
  await page.keyboard.press('Control+End');
  await page.keyboard.insertText(extra);
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser');
  await page.click('#lite-build');
}

test('a speech block that is too long is refused on the page with the length it has', async ({ page }) => {
  // 40 bars at tempo 100 is 96 seconds, over the 60 seconds of one block
  await buildWith(page, '\nspeech long at 1 for 40 {\n  text "hello"\n}\n');
  await expect(page.locator('#lite-log')).toContainText('Stopped: The speech block "long" is 96 seconds long', { timeout: 60000 });
});

test('more speech blocks than Remix allows are refused on the page @quarantine', async ({ page }) => {
  await buildWith(page, Array.from({ length: 8 }, (_, i) => `
speech extra${i} {
  text "hello"
}
`).join(''));
  await expect(page.locator('#lite-log')).toContainText('Stopped: A Remix build allows at most 8 speech blocks.', { timeout: 60000 });
});

test('Listen in Remix lights the word that sounds in the text of the speech block', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'speech-hello');
  await page.click('#lite-listen-play');
  await expect(page.locator('#lite-listen-status')).toContainText('hearing build', { timeout: 60000 });
  const seen = new Set<string>();
  await expect.poll(async () => {
    for (const text of await page.locator('.cm-content .lp-now').allInnerTexts()) seen.add(text.trim());
    return seen.size;
  }, { timeout: 25000, intervals: [60] }).toBeGreaterThanOrEqual(2);
  const line = await page.locator('#lite-editor .cm-content').innerText();
  for (const word of seen) expect(line, `"${word}" is a word of the text`).toContain(word);
  await page.click('#lite-listen-play');
  await expect(page.locator('.cm-content .lp-now')).toHaveCount(0);
});
