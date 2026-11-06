import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText, setSrc } from '../../web/tests/support';

freshServer({ song: 'composition "broken" { ample 1  tempo 92  master-seed 1 }\nmidi-clip chords {\n  chords Am F\n}\npiano piano pinned@2.0 {\n  from chordz.notes -> notes\n}\n' });

test('a project that opens with a mistake in one block keeps the Blocks view usable and marks only that block red', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(page.locator('#se')).toBeHidden();   // the text can be read into blocks: no need to start in the code view
  await expect(page.locator('#robanner')).toBeHidden();
  await expect(page.locator('.node.err')).toHaveCount(1);   // the piano reads from a block that does not exist
  await expect(page.locator('.node.err .node-errmark')).toBeVisible();
  await page.click('#vs-source');
  await expect(page.locator('#se-prob .seerr.error').first()).toBeVisible();
  await setSrc(page, (await srcText(page)).replace('chordz', 'chords'));
  await expect(page.locator('#se-status')).toContainText('0 error(s)');
  await page.click('#se-apply');
  await page.click('#vs-blocks');
  await expect(page.locator('.node').first()).toBeVisible();
  await expect(page.locator('.node.err')).toHaveCount(0);   // the value is right again: the mark is gone
});

test('a text that cannot be read into blocks greys the Blocks view out, with a link to the code view', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await setSrc(page, (await srcText(page)) + '\nsynth lead at 1 for 4 {\n  wave saw\n');   // a block that is not closed
  await page.click('#vs-blocks');
  await expect(page.locator('#robanner')).toBeVisible();
  await expect(page.locator('#canvaswrap.broken')).toHaveCount(1);
  await page.click('#ro-back');   // Fix in the code view
  await expect(page.locator('#se')).toBeVisible();
});
