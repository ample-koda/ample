import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// The musical types in the source editor (docs/language/15-musical-types.md): the completion knows the type of the argument, the signature bar shows the real types, the checker underlines the wrong ones.
freshServer({ song: 'composition "p" { ample 1  tempo 100  master-seed 3 }\nmidi-clip lead at 1 for 1 {\n  notes "1:0:1:60:100"\n}\n' });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.click('#vs-source'); await expect.poll(() => srcText(page)).toMatch(/composition/); await page.locator('#se-cm .cm-content').click(); await page.keyboard.press('Control+End'); });

const open = async (page) => expect(async () => { await page.keyboard.press('Control+Space'); await expect(page.locator('#se-comp')).toBeVisible({ timeout: 1500 }); }).toPass({ timeout: 20000 });

test('walk( offers pitch classes and the second argument only scales', async ({ page }) => {
  await page.keyboard.type('midi-clip m at 2 for auto {\n  notes walk(C, ');
  await open(page);
  await expect(page.locator('#se-comp')).toContainText('dorian');
  await expect(page.locator('#se-comp')).not.toContainText('F#');
  await page.keyboard.type('mi');
  await expect(page.locator('#se-comp')).toContainText('minor');
  await page.keyboard.press('Enter');
  await expect.poll(() => srcText(page)).toContain('walk(C, minor');
});

test('the signature bar shows the real types and the active parameter', async ({ page }) => {
  await page.keyboard.type('midi-clip m at 2 for auto {\n  notes walk(C, minor, step=');
  const bar = page.locator('#se-sig');
  await expect(bar).toBeVisible();
  await expect(bar).toContainText('PitchClass root');
  await expect(bar).toContainText('Duration step = 0.5beat');
  await expect(bar.locator('strong')).toHaveText('Duration step = 0.5beat');
  await page.keyboard.type('0.5beat) |> shift(2');
  await expect(bar).toContainText('Duration by');
  await expect(bar).toContainText('needs a unit');
});

test('a wrong argument is found before anything runs, with its own message', async ({ page }) => {
  await page.keyboard.type('midi-clip m at 2 for auto {\n  notes walk(123, minor, seed=1)\n}\n');
  await expect(page.locator('#se-prob')).toContainText('AMP2044', { timeout: 20000 });
  await expect(page.locator('#se-prob')).toContainText('not a PitchClass');
});
