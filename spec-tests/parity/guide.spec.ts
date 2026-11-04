import { test, expect } from '@playwright/test';
import { launchUrl, openApp } from '../../web/tests/support';

// The guide (docs/guide, served at /guide): pages, the dictionary, the tooltips of the terms and the safety of the static route.
const guide = (path = '') => `${new URL(launchUrl()).origin}/guide/${path}`;

test('the guide opens from the header, lists its pages and the pages lead to each other', async ({ page }) => {
  await openApp(page);
  await page.locator('#btn-help').click();   // the guide is in the help menu
  const link = page.getByRole('menuitem', { name: 'Guide', exact: true });
  await expect(link).toHaveAttribute('href', '/guide/');
  await page.goto(guide());
  await expect(page.locator('h1')).toHaveText('AMPLE guide');
  await page.getByRole('link', { name: /Bars and time/ }).first().click();
  await expect(page.locator('h1')).toContainText('Bars and time');
  await expect(page.locator('figure img').first()).toBeVisible();
  expect(await page.locator('figure img').first().evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 100)).toBe(true);
  await page.locator('.pager a').last().click();
  await expect(page.locator('h1')).toContainText('Build, play and stop');
});

test('a word with a dotted underline shows its meaning on hover and leads to the dictionary, which says where it is used', async ({ page }) => {
  await page.goto(guide('bars-and-time.html'));
  const bar = page.locator('a.term[data-term="bar"]').first();
  await bar.hover();
  const card = page.locator('#termcard');
  await expect(card).toBeVisible();
  await expect(card).toContainText('A bar (also called a measure)');
  await bar.click();
  await expect(page).toHaveURL(/glossary\.html#bar$/);
  const entry = page.locator('#bar.entry');
  await expect(entry).toBeVisible();
  await expect(entry).toContainText('Used in:');
  await expect(entry.getByRole('link', { name: 'Bars and time' })).toBeVisible();
});

test('only files of the guide folder are served', async ({ request }) => {
  expect((await request.get(guide('glossary.html'))).status()).toBe(200);
  expect((await request.get(guide('img/welcome-window.png'))).headers()['content-type']).toContain('image/png');
  expect((await request.get(guide('nope.html'))).status()).toBe(404);
  expect((await request.get(guide('..%2f..%2fpackage.json'))).status()).toBe(404);
  expect((await request.get(guide('../../package.json'))).status()).toBe(404);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 800 }, hasTouch: true });

  test('the menu is a button that opens the list of pages and closes again', async ({ page }) => {
    await page.goto(guide('bars-and-time.html'));
    const nav = page.locator('#guide-nav');
    const button = page.getByRole('button', { name: /Menu/ });
    await expect(button).toBeVisible();
    await expect(nav).toBeHidden();
    await button.tap();
    await expect(nav).toBeVisible();
    await expect(button).toHaveAttribute('aria-expanded', 'true');
    expect((await nav.getByRole('link', { name: 'Dictionary' }).boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await nav.getByRole('link', { name: 'Effects' }).tap();
    await expect(page.locator('h1')).toHaveText('Effects');
    await expect(page.locator('#guide-nav')).toBeHidden();
    await page.getByRole('button', { name: /Menu/ }).tap();
    await page.keyboard.press('Escape');
    await expect(page.locator('#guide-nav')).toBeHidden();
  });

  test('no page scrolls sideways, and a tapped word shows its meaning before it opens the dictionary', async ({ page }) => {
    for (const name of ['bars-and-time', 'effects', 'shortcuts', 'sequencer', 'glossary', 'code-examples']) {
      await page.goto(guide(`${name}.html`));
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(overflow, name).toBeLessThanOrEqual(0);
    }
    await page.goto(guide('bars-and-time.html'));
    const bar = page.locator('a.term[data-term="bar"]').first();
    await bar.tap();
    await expect(page.locator('#termcard')).toBeVisible();
    await expect(page).toHaveURL(/bars-and-time\.html$/);
    await bar.tap();
    await expect(page).toHaveURL(/glossary\.html#bar$/);
  });
});
