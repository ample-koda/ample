import { test, expect } from '@playwright/test';
import { bottomTab, freshServer, openApp } from '../../web/tests/support';

// A block that cannot be built shows a red badge in the header, says so in the build log and Play offers to build again.
freshServer({ song: 'composition "bad" { ample 1  tempo 100  master-seed 1 }\nsequencer sq at 1 for 2 {\n}\n' });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

test('a failed block gives a badge that opens the log, a summary line in the log and a Play that builds again', async ({ page }) => {
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  await expect(page.locator('#b-failed')).toHaveText('1 failed');
  await expect(page.locator('#b-failed')).toHaveAttribute('title', /sq/);
  await expect(page.locator('#btn-preview')).toHaveText('▶ Build and play');
  await page.click('#b-failed');
  await expect(page.locator('#blog')).toContainText('Build finished with 1 failed block: sq.');
  await bottomTab(page, 'log');
});
