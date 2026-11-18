import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// AMPLE Remix and the log view (plan/diagnostics WP8): the Song log tab chooses a log level and assertions for the next builds of this page,
// a build brings the events of the song, and nothing of it is written into the song. Pixel Quest is used because it needs no sound bank.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/#token=');

async function fresh(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
}

test('the Song log of Remix shows the events of a build at the chosen level, and the song is not changed', async ({ page }) => {
  await fresh(page);
  await page.click('#lite-demos-btn');
  await page.locator('#lite-demo-list li[data-demo="pixel-quest"] button').click();
  await expect(page.locator('#lite-demo-dialog')).toBeHidden();
  await expect(page.locator('#lite-editor .cm-content')).toContainText('PIXEL QUEST');   // the editor takes the demo a moment after the dialog closes
  const before = await page.locator('#lite-editor .cm-content').innerText();

  // the default build keeps no log: the empty state says why
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).toContainText('seconds', { timeout: 90000 });
  await page.click('#lite-tab-songlog');
  await expect(page.locator('#lite-songlog-pane')).toBeVisible();
  await expect(page.locator('#lite-songlog .lite-songlog-row')).toHaveCount(0);
  await expect(page.locator('#lite-songlog')).toContainText('info, debug or trace');

  // a choice applies at the next build, and the page says so
  await page.selectOption('#lite-log-level', 'debug');
  await expect(page.locator('#lite-songlog-note')).toContainText('Build again');
  await page.click('#lite-build');
  await expect(page.locator('#lite-songlog-note')).toHaveText('', { timeout: 90000 });
  const row = page.locator('#lite-songlog .lite-songlog-row.debug', { hasText: 'progression' });
  await expect(row).toHaveCount(1);
  await expect(page.locator('#lite-songlog-filters [data-level="debug"]')).toHaveText('debug 1');

  // the level filter hides it; the song text is what the demo is
  await page.locator('#lite-songlog-filters [data-level="debug"]').click();
  await expect(row).toHaveCount(0);
  expect(await page.locator('#lite-editor .cm-content').innerText()).toBe(before);
});

test('Watch it think builds and explains itself with made text at every level', async ({ page }) => {
  test.setTimeout(300000);
  await fresh(page);
  await page.click('#lite-demos-btn');
  await page.locator('#lite-demo-list li[data-demo="watch-it-think"] button').click();
  await expect(page.locator('#lite-demo-dialog')).toBeHidden();

  // trace shows every level; the messages are built with +, String(...) and sprintf
  await page.click('#lite-tab-songlog');
  await page.selectOption('#lite-log-level', 'trace');
  await page.click('#lite-build');
  await expect(page.locator('#lite-output-info')).not.toHaveText(/Nothing built|Building/, { timeout: 180000 });
  // the song itself has no errors; the only one allowed is the shared General MIDI bank missing on a test server (the server test plays it)
  const built = (await page.locator('#lite-output-info').innerText()).includes('seconds');
  if (built) await expect.poll(() => page.locator('#lite-output').evaluate((a) => (a as HTMLAudioElement).src)).not.toBe('');
  else {
    await expect(page.locator('#lite-problems')).toContainText('AMP5311');
    const errors = await page.locator('#lite-problems').evaluate((e) => [...e.textContent!.matchAll(/Error (AMP\d{4})/g)].map((m) => m[1]));
    expect(errors).toEqual(['AMP5311']);
  }
  if (!(await page.locator('#lite-songlog-pane').isVisible())) await page.click('#lite-tab-songlog');
  const log = page.locator('#lite-songlog');
  await expect(log).toContainText('chorus lifted 5 semitones', { timeout: 90000 });   // + joins text and a number
  await expect(log).toContainText('plan: lift 5 of 12 (42% of an octave)');   // sprintf %d and %.0f%%
  await expect(log).toContainText('round 2 / 8: up 02 semitones');   // sprintf %02d
  await expect(log).toContainText('voicing 5 does not exist');

  // a lower level hides the debug and trace lines at the next build
  await page.selectOption('#lite-log-level', 'info');
  await page.click('#lite-build');
  await expect(page.locator('#lite-songlog-note')).toHaveText('', { timeout: 180000 });
  await expect(log).toContainText('chorus lifted 5 semitones');
  await expect(log).not.toContainText('round 2 / 8');
});
