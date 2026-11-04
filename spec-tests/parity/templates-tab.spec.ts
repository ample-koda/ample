import { test, expect } from '@playwright/test';
import { openApp, srcText } from '../../web/tests/support';

// The Templates tab (third tab of the left panel, icon only): the beats of the built-in catalogue, searchable; a double click opens the wizard of a beat, which adds a drums block.
test.beforeEach(async ({ page }) => { await openApp(page); await page.locator('#ltabs button[data-l="tpl"]').click(); });

test('the three tabs are icons with a name, and the Templates tab lists the built-in beats', async ({ page }) => {
  for (const name of ['Block library', 'Structure', 'Templates']) await expect(page.getByRole('tab', { name })).toBeVisible();
  expect((await page.locator('#ltabs button').first().textContent())!.trim()).toBe('');   // an icon, no text
  const cards = page.locator('#tpl-list .tpl-card');
  await expect(cards.first()).toBeVisible();
  expect(await cards.count()).toBeGreaterThan(10);
  await expect(page.locator('#pane-lib')).toBeHidden();
});

test('searching narrows the list and a double click opens the wizard of the beat', async ({ page }) => {
  const cards = page.locator('#tpl-list .tpl-card');
  await expect(cards.first()).toBeVisible();
  const all = await cards.count();
  await page.locator('#tpl-q').fill('house');
  await expect.poll(() => cards.count()).toBeLessThan(all);
  const name = (await cards.first().locator('b').textContent())!;
  await cards.first().dblclick();
  const win = page.locator('dialog.ample-win');
  await expect(win).toBeVisible();
  await expect(win.locator('.aw-title')).toContainText(name);
  await expect(win.locator('.bw-say')).not.toBeEmpty();
  await expect(win.getByRole('button', { name: 'Add to song' })).toBeVisible();
});

test('the wizard adds one drums block with the beat, and closing without changes just closes it', async ({ page }) => {
  const cards = page.locator('#tpl-list .tpl-card');
  await cards.first().dblclick();
  const win = page.locator('dialog.ample-win');
  await expect(win).toBeVisible();
  await win.locator('.aw-close').click();
  await expect(win).toHaveCount(0);   // nothing changed: no question
  await cards.first().dblclick();
  await win.locator('input[aria-label="Block name"]').fill('mybeat');
  await win.getByRole('button', { name: 'Add to song' }).click();
  await expect(win).toHaveCount(0);
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/drums mybeat (at \d+ for \d+ )?\{/);
  await expect.poll(() => srcText(page)).toMatch(/patterns groove=/);
});

test('closing the wizard after a change asks, and Do not add leaves the song as it was', async ({ page }) => {
  await page.locator('#tpl-list .tpl-card').first().dblclick();
  const win = page.locator('dialog.ample-win');
  await win.locator('input[aria-label="Block name"]').fill('changed');
  await win.locator('.aw-close').click();
  await expect(page.getByRole('alertdialog')).toBeVisible();
  await page.getByRole('button', { name: 'Do not add' }).click();
  await expect(win).toHaveCount(0);
  await page.click('#vs-source');
  expect(await srcText(page)).not.toMatch(/drums changed/);
});

test('a groove-set can bring chords and bass: the tick boxes add a piano with the chords and a bass on their roots', async ({ page }) => {
  await page.locator('#tpl-q').fill('house');
  const cards = page.locator('#tpl-list .tpl-card');
  await expect(cards.first()).toBeVisible();
  await cards.filter({ hasText: 'four on the floor' }).first().dblclick();
  const win = page.locator('dialog.ample-win');
  await expect(win).toBeVisible();
  await expect(win.locator('#bw-chords')).not.toBeChecked();   // drums only by default
  await win.locator('#bw-chords').check();
  await win.locator('#bw-bass').check();
  await win.locator('input[aria-label="Block name"]').fill('club');
  await win.getByRole('button', { name: 'Add to song' }).click();
  await expect(win).toHaveCount(0);
  await page.click('#vs-source');
  const text = () => srcText(page);
  await expect.poll(text).toMatch(/midi-clip club-chords/);
  await expect.poll(text).toMatch(/piano club-keys/);
  await expect.poll(text).toMatch(/midi-clip club-bassline/);
  await expect.poll(text).toMatch(/bass club-bass/);
  await expect.poll(text).toMatch(/from club-chords\.notes -> notes/);
});

test('what the wizard writes has no errors for the language', async ({ page }) => {
  await page.locator('#tpl-q').fill('house');
  await page.locator('#tpl-list .tpl-card').filter({ hasText: 'four on the floor' }).first().dblclick();
  const win = page.locator('dialog.ample-win');
  await win.locator('#bw-chords').check();
  await win.locator('#bw-bass').check();
  await win.getByRole('button', { name: 'Add to song' }).click();
  await expect(win).toHaveCount(0);
  await expect.poll(() => page.evaluate(async () => {
    const { S } = await import('/app/store.js');
    const errors = (S.doc?.diagnostics || []).filter((d: { severity: string }) => d.severity === 'error');
    return JSON.stringify(errors.map((d: { code: string; message: string }) => `${d.code} ${d.message}`));
  })).toBe('[]');
});
