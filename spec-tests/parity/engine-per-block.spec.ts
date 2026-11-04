import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp, openTab } from '../../web/tests/support';

// Plan engine-per-block: the Parameters panel of an ACE block chooses a place and a model and writes engine "route:model" into the song.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const engineOf = async (block: string) => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block)?.parameters.find((p: { name: string }) => p.name === 'engine')?.value as string | undefined;

test('the engine of an ace-music block is chosen with a place and a model and written to the song', async ({ page }) => {
  await addTemplateBlock(page, 'ace-music', 'tune');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'tune').click({ position: { x: 20, y: 36 } });
  await openTab(page, 'parameters');
  const route = page.locator('.engine-route');
  const model = page.locator('.engine-model');
  await expect(route).toBeVisible();
  await expect(model).toBeDisabled();   // a model needs a place
  await route.selectOption('faer');
  await expect.poll(() => engineOf('tune')).toBe('"faer"');
  await expect(model).toBeEnabled();
  await model.fill('xl-sft');
  await model.press('Enter');
  await model.blur();
  await expect.poll(() => engineOf('tune')).toBe('"faer:xl-sft"');
  await route.selectOption('');
  await expect.poll(() => engineOf('tune')).toBeUndefined();   // back to the project default: the line is removed
  await expect(model).toBeDisabled();
});

test('a voice-clone block takes only a place, with no model field', async ({ page }) => {
  await addTemplateBlock(page, 'voice-clone', 'say');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'say').click({ position: { x: 20, y: 36 } });
  await openTab(page, 'parameters');
  await expect(page.locator('.engine-route')).toBeVisible();
  await expect(page.locator('.engine-model')).toHaveCount(0);
  await page.locator('.engine-route').selectOption('local');
  await expect.poll(() => engineOf('say')).toBe('"local"');
});
