import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText, setSrc } from '../../web/tests/support';

freshServer({ song: 'composition "broken" { ample 1  tempo 92  master-seed 1 }\nmidi-clip chords {\n  chords Am F\n}\npiano piano pinned@2.0 {\n  from chordz.notes -> notes\n}\n' });

test('a project that opens with mistakes starts in the source editor and the node editor is read-only until they are fixed', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(page.locator('#se')).toBeVisible();
  await expect(page.locator('#se-prob .seerr.error').first()).toBeVisible();
  await page.click('#vs-blocks');
  await expect(page.locator('#robanner')).toBeVisible();
  await page.click('#ro-back');
  await setSrc(page, (await srcText(page)).replace('chordz', 'chords'));
  await expect(page.locator('#se-status')).toContainText('0 error(s)');
  await page.click('#se-apply');
  await page.click('#vs-blocks');
  await expect(page.locator('.node').first()).toBeVisible();
  await expect(page.locator('#robanner')).toBeHidden();
});
