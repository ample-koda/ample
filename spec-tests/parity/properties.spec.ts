import { test, expect } from '@playwright/test';
import { node, openApp, state, openTab, srcText, setSrc } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); });

test('a property edit writes the source and a source edit updates the panel', async ({ page }) => {
  await node(page, 'chords').locator('.nh .t').click();
  const bars = page.locator('#props .prop', { hasText: 'bars' }).locator('input[type="number"]');
  await bars.fill('12');
  await bars.press('Tab');
  await expect.poll(async () => (await state(page)).text).toContain('bars 12');
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/bars 12/);
  await setSrc(page, (await srcText(page)).replace('bars 12', 'bars 14'));
  await expect(page.locator('#se-status')).toContainText('0 error(s)');
  await page.click('#se-apply');
  await page.click('#vs-blocks');
  // the block is chosen again until its properties show: the canvas can be drawn again right after the view changes
  await expect(async () => {
    await node(page, 'chords').locator('.nh .t').click();
    await expect(page.locator('#props .prop', { hasText: 'bars' }).locator('input[type="number"]')).toHaveValue('14', { timeout: 3000 });
  }).toPass({ timeout: 20_000 });
});

test('the flags and the title are properties too, and the project settings open from the panel', async ({ page }) => {
  await node(page, 'drums').locator('.nh .t').click();
  await openTab(page, 'info');
  await page.locator('#props label', { hasText: 'locked' }).locator('input').check();
  await expect.poll(async () => (await state(page)).text).toMatch(/drums drums at 1 for 16 as "beat A" lock \{/);
  await page.locator('#cv').click({ position: { x: 5, y: 5 } });
  await page.click('#open-settings');
  await expect(page.locator('#pm')).toBeVisible();
  await expect(page.locator('#ps-budget')).toBeVisible();
});

test('a template-declared long field uses the expanded editor and preserves line breaks as source escapes', async ({ page }) => {
  await node(page, 'drums').locator('.nh .t').click();
  await openTab(page, 'parameters');
  const property = page.locator('#props .prop-tabpanel.active .prop').filter({ has: page.locator('label', { hasText: /(^|\s)pattern\s*♪$/ }) });
  await expect(property.locator('input')).toHaveValue('beat-A');
  await property.locator('button', { hasText: '…' }).click();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await page.locator('#dlg-extra textarea').fill('kick\nsnare');
  await page.locator('#dlg-btns button', { hasText: 'Apply' }).click();
  await expect.poll(async () => (await state(page)).text).toContain('pattern kick\\nsnare');
});

test('a string property shows its text without quotes and writes the quotes back (the source keeps them)', async ({ page }) => {
  // the chords line gets its quotes through an edit, as a song written with quotes has them
  expect(await page.evaluate(async () => { const m = await import('/app/store.js'); return m.edit([{ type: 'setParameter', block: 'chords', name: 'chords', value: JSON.stringify('Am F C G') }]); })).toBe(true);
  await node(page, 'chords').locator('.nh .t').click();
  const field = page.locator('#props .prop').filter({ has: page.locator('label', { hasText: /^chords/ }) }).locator('input:not([type="number"]):not([type="checkbox"]):not([type="range"]), textarea').first();
  await expect(field).toHaveValue(/^[A-G][^"]*$/);   // no quotes in the field

  await field.evaluate((e) => { const input = e as HTMLInputElement; input.value = 'Am F C G7'; input.dispatchEvent(new Event('change', { bubbles: true })); });
  await expect.poll(async () => (await state(page)).text).toContain('chords "Am F C G7"');
});
