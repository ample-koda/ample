import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// Autocomplete in the source editor: a grey preview while typing that Tab takes, Escape hides it for the line, Ctrl+Space opens the list.
freshServer({ song: 'composition "beats" { ample 1  tempo 100  master-seed 3 }\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\n' });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.click('#vs-source'); await expect.poll(() => srcText(page)).toMatch(/composition/); await page.locator('#se-cm .cm-content').click(); await page.keyboard.press('Control+End'); });

const ghost = (page) => page.locator('#se-ghost .gh');

test('a grey preview follows the typing, Tab takes it, and the header tail and view lines are known', async ({ page }) => {
  await page.keyboard.type('\nmidi-c');
  await expect(ghost(page)).toHaveText('lip');
  await page.keyboard.press('Tab');
  await expect.poll(() => srcText(page)).toMatch(/midi-clip midi-clip {/);
  await expect(ghost(page)).toHaveCount(0);
  await page.keyboard.type('\n# view p');
  await expect(page.locator('#se-ghost .gh')).toHaveCount(0);   // "# view p": the word is "p", but only "view" and its parts are known after a space
  await page.keyboard.type('in ');
  await page.keyboard.type('dr');
  await expect(ghost(page)).toHaveText('ums');
});

test('Escape hides the preview until another line or a space; Ctrl+Space opens the list to choose from', async ({ page }) => {
  await page.keyboard.type('\nmast');
  await expect(ghost(page)).toHaveText('er');
  await page.keyboard.press('Escape');
  await expect(ghost(page)).toHaveCount(0);
  await page.keyboard.type('e');   // same line: still quiet
  await page.waitForTimeout(600);
  await expect(ghost(page)).toHaveCount(0);
  await page.keyboard.press('Control+Space');
  await expect(page.locator('#se-comp')).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Escape');
  await expect(page.locator('#se-comp')).toBeHidden();
  await page.keyboard.type('\nbas');   // another line: it is back
  await expect(ghost(page)).toHaveText('s');
});

test('the list opens under the word being completed, not in a fixed place', async ({ page }) => {
  await page.keyboard.type('\nbass b {\n  from ');
  await expect(async () => { await page.keyboard.press('Control+Space'); await expect(page.locator('#se-comp')).toBeVisible({ timeout: 1500 }); }).toPass({ timeout: 20000 });   // the completion server may still be starting
  const comp = (await page.locator('#se-comp').boundingBox())!, area = (await page.locator('#se-cm .cm-scroller').boundingBox())!;
  const line = (await srcText(page)).split('\n').length - 1;
  const caretY = area.y + 6 + line * 18 - (await page.locator('#se-ta').evaluate((e) => e.scrollTop));
  expect(Math.abs(comp.y - (caretY + 18)) < 6 || Math.abs(comp.y + comp.height - caretY) < 6).toBe(true);
  expect(comp.x).toBeGreaterThan(area.x + 40);   // after the indent and "from ", not at the left edge
});

test('an open list follows what is typed, and the chosen row stays in view when you walk down it', async ({ page }) => {
  await page.keyboard.type('\nd');
  await page.keyboard.press('Control+Space');
  const rows = page.locator('#se-comp div');
  await expect(rows.first()).toBeVisible();
  const before = await rows.count();
  await page.keyboard.type('r');
  // the list is made again for "dr": fewer rows, and every one of them has it
  await expect.poll(() => rows.count()).toBeLessThan(before);
  for (const text of await rows.locator('span').allTextContents()) expect(text.toLowerCase()).toContain('dr');
  await page.keyboard.press('Backspace');
  await expect.poll(() => rows.count()).toBe(before);
  // walking down: the chosen row is always inside the visible part of the list
  for (let i = 0; i < before + 1; i++) {
    await page.keyboard.press('ArrowDown');
    const inside = await page.evaluate(() => {
      const list = document.querySelector('#se-comp')!.getBoundingClientRect(), on = document.querySelector('#se-comp div.on')!.getBoundingClientRect();
      return on.top >= list.top - 1 && on.bottom <= list.bottom + 1;
    });
    expect(inside).toBe(true);
  }
  await expect(page.locator('#se-comp')).toBeVisible();   // the list is still open: the arrow keys never went back to the text
});
