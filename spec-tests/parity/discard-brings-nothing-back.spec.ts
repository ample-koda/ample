import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText, setSrc } from '../../web/tests/support';

// Unsaved changes that were thrown away stay away: Revert, and Discard when a tab closes, also remove the copy that this browser keeps of the song (ample.work), or the next open says
// "Your unsaved changes from this browser are back" and puts the thrown-away text in the editor again.
freshServer({});

const copies = (page) => page.evaluate(() => Object.entries(localStorage).filter(([k]) => k.startsWith('ample.work:') || k.startsWith('ample.source-draft:')).map(([k, v]) => ({ key: k, typed: String(v).includes('typed by the test') })));
const typedIn = (list) => list.some((c) => c.typed);

async function typeSomething(page) {
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const original = await srcText(page);
  await setSrc(page, `${original}${String.fromCharCode(10)}# typed by the test${String.fromCharCode(10)}`);
  await expect.poll(async () => typedIn(await copies(page))).toBe(true);   // the browser keeps what was typed
}

test('Revert in the source editor removes the copy of this browser', async ({ page }) => {
  await openApp(page, { reset: false });
  await typeSomething(page);
  await page.click('#se-revert');
  await page.locator('#dlg button:has-text("Revert")').click();
  await expect.poll(async () => typedIn(await copies(page))).toBe(false);
  await page.reload();
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  expect(await srcText(page)).not.toContain('typed by the test');
  expect(typedIn(await copies(page))).toBe(false);
});

test('Discard when a tab closes removes the copy, and the song opens without the thrown-away text', async ({ page }) => {
  await openApp(page, { reset: false });
  await typeSomething(page);
  await page.keyboard.press('Control+Enter');   // applied: the working copy is changed and the tab asks when it closes
  await page.waitForTimeout(800);
  await setSrc(page, `${await srcText(page)}# more${String.fromCharCode(10)}`);
  await page.locator('.tab-x').first().click();
  await page.locator('#dlg button:has-text("Discard")').first().click();
  await expect.poll(async () => typedIn(await copies(page))).toBe(false);
  await page.waitForTimeout(1500);   // a copy that is written again would be there by now
  expect(typedIn(await copies(page))).toBe(false);
  await page.reload();
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  expect(await srcText(page)).not.toContain('typed by the test');
  await expect(page.locator('#toast')).not.toContainText('are back');
});
