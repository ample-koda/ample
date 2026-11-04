import { test, expect } from '@playwright/test';
import { openApp, srcText } from '../../web/tests/support';

// A problem of a line with a squiggle opens in the same little box as a build error, with a double click on it in the problem list (not on hover).
test.beforeEach(async ({ page }) => { await openApp(page); await page.click('#vs-source'); await expect.poll(() => srcText(page)).toMatch(/composition/); });

test('a double click on a problem shows what is wrong', async ({ page }) => {
  await page.locator('#se-cm .cm-content').click();
  await page.keyboard.press('Control+End');
  await page.keyboard.type('\nmixer mixx {\n  from nothere.audio -> audio1\n}');
  await expect(page.locator('.cm-content .sq').first()).toBeVisible({ timeout: 15000 });
  // a plain hover shows nothing any more (too heavy): the details of a problem open with a double click on it in the list
  const sq = await page.locator('.cm-content .sq').first().boundingBox();
  await page.mouse.move(sq!.x + 5, sq!.y + sq!.height / 2);
  await page.mouse.move(sq!.x + 8, sq!.y + sq!.height / 2);
  await page.waitForTimeout(500);
  await expect(page.locator('#se-errtip')).toBeHidden();
  await page.locator('#se-prob .seerr').first().dblclick();
  await expect(page.locator('.et-msg').first()).not.toBeEmpty();
  await expect(page.locator('.et-head b').first()).toHaveText(/Error|Warning|Note/);
});
