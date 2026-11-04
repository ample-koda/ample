import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// Autocomplete for note pipelines in the source editor: the operators after |>, the clips a notes line can read.
freshServer({ song: 'composition "p" { ample 1  tempo 100  master-seed 3 }\nmidi-clip lead at 1 for 1 {\n  notes "1:0:1:60:100"\n}\n' });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.click('#vs-source'); await expect.poll(() => srcText(page)).toMatch(/composition/); await page.locator('#se-cm .cm-content').click(); await page.keyboard.press('Control+End'); });

const open = async (page) => expect(async () => { await page.keyboard.press('Control+Space'); await expect(page.locator('#se-comp')).toBeVisible({ timeout: 1500 }); }).toPass({ timeout: 20000 });

test('after |> the list offers the operators and one of them is taken with its arguments', async ({ page }) => {
  await page.keyboard.type('midi-clip b at 2 for auto {\n  notes lead.notes\n    |> tra');
  await open(page);
  await expect(page.locator('#se-comp')).toContainText('transpose');
  await page.keyboard.press('Enter');
  await expect.poll(() => srcText(page)).toContain('|> transpose(+12)');
});

test('the list after |> has the new operators and filters by what is typed', async ({ page }) => {
  await page.keyboard.type('midi-clip b at 2 for auto {\n  notes lead.notes\n    |> ');
  await open(page);
  for (const name of ['bars', 'concat', 'humanize', 'legato', 'bake']) await expect(page.locator('#se-comp')).toContainText(name);
  await page.keyboard.type('hum');
  await expect.poll(() => page.locator('#se-comp div').count()).toBeLessThan(4);
  await expect(page.locator('#se-comp')).toContainText('humanize');
});

test('a notes line offers the clips it can read', async ({ page }) => {
  await page.keyboard.type('midi-clip b at 2 for auto {\n  notes le');
  await open(page);
  await expect(page.locator('#se-comp')).toContainText('lead.notes');
});
