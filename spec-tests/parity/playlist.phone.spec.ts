import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

const showTab = async (page: import('@playwright/test').Page) => {
  const tab = page.locator('#btabs button[data-t="playlist"]');
  const shown = await page.evaluate(() => document.querySelector('#b-playlist')?.classList.contains('on') && !document.querySelector('#bottom')?.classList.contains('min'));
  if (!shown) await tab.click();
};

// PV09: on a phone the Playlist is for reading: it shows, it scrolls, nothing in it edits the song, and nothing sticks out of the screen.
freshServer();
test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('ample.playlist.on', '1'));
  await openApp(page);
});

test('playlist phone: the tab is there, shows the lines, and is read-only', async ({ page }) => {
  await showTab(page);
  await expect(page.locator('#b-playlist .pl-line').first()).toBeVisible();
  await expect(page.locator('#b-playlist .pl-toolbar')).toContainText('Read-only on a phone');
  const before = await page.evaluate(() => (window as any).AMPLE_STATE.doc.text);
  const clip = page.locator('#b-playlist .pl-clip').first();
  await clip.focus();
  await clip.press('ArrowRight');
  await clip.press('Delete');
  await page.waitForTimeout(400);
  expect(await page.evaluate(() => (window as any).AMPLE_STATE.doc.text)).toBe(before);
  const width = await page.evaluate(() => document.documentElement.scrollWidth);
  expect(width).toBeLessThanOrEqual(391);
});

test('playlist phone: the names of the lines are readable and the timeline scrolls inside its own box', async ({ page }) => {
  await showTab(page);
  const label = page.locator('#b-playlist .pl-line .pl-name').first();
  await expect(label).toBeVisible();
  expect(Number(await label.evaluate((e) => parseFloat(getComputedStyle(e).fontSize)))).toBeGreaterThanOrEqual(11);
  const scroll = page.locator('#b-playlist .pl-scroll');
  expect(await scroll.evaluate((e) => getComputedStyle(e).overflowX)).toMatch(/auto|scroll/);
});
