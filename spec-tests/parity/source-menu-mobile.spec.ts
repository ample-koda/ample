import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// On a phone the long press that selects code also opens the menu of the editor. The keyboard goes away while the menu is open, and the menu closes when the page scrolls or the keyboard opens.
freshServer();
test.use({ hasTouch: true, isMobile: true, viewport: { width: 390, height: 844 } });

const open = (page) => page.locator('#se-ta').evaluate((e) => {
  const ta = e as HTMLTextAreaElement;
  ta.focus(); ta.setSelectionRange(0, 7);
  ta.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 120, clientY: 500 }));
});

test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
});

test('the keyboard goes away while the menu of the editor is open, and comes back with a tap in the editor', async ({ page }) => {
  await open(page);
  await expect(page.locator('#menu')).toBeVisible();
  await expect(page.locator('#se-cm .cm-content')).toHaveAttribute('inputmode', 'none');
  await page.locator('#se-cm').dispatchEvent('pointerdown');
  await expect(page.locator('#se-cm .cm-content')).toHaveAttribute('inputmode', 'text');
});

test('the menu closes when the page scrolls', async ({ page }) => {
  await open(page);
  await expect(page.locator('#menu')).toBeVisible();
  await page.waitForTimeout(500);
  await page.locator('#se-cm .cm-scroller').evaluate((e) => e.dispatchEvent(new Event('scroll')));
  await expect(page.locator('#menu')).toBeHidden();
});

test('the menu closes when the keyboard opens (the visible part of the page shrinks)', async ({ page }) => {
  await open(page);
  await expect(page.locator('#menu')).toBeVisible();
  await page.waitForTimeout(500);
  await page.evaluate(() => window.visualViewport?.dispatchEvent(new Event('resize')));
  await expect(page.locator('#menu')).toBeHidden();
});
