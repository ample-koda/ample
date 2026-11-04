import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// The context menu of Koda: the model and the thinking level of this Koda session. A choice is an override (shown by a chip), "Use default" removes it, "Set as default for Koda" saves it; Koda looks the part.
test.beforeEach(async ({ page }) => {
  await page.route('**/api/ai/models', (route) => route.fulfill({ json: { default: 'gpt-5-codex', models: [
    { id: 'gpt-5-codex', name: 'GPT-5 Codex', description: 'the standard model', efforts: ['low', 'medium', 'high'], defaultEffort: 'medium', isDefault: true },
    { id: 'gpt-5-codex-mini', name: 'GPT-5 Codex mini', description: 'small and fast', efforts: ['low', 'medium'], defaultEffort: 'low' },
    { id: 'gpt-6-pro', name: 'GPT-6 Pro', description: 'the most capable model', efforts: ['low', 'medium', 'high', 'xhigh'], defaultEffort: 'high' },
  ], usage: {} } }));
  await openApp(page);
  await page.locator('#btn-koda').click();
  await expect(page.locator('#koda-layer .pet')).toBeVisible();
});

const menu = (page) => page.locator('#menu');
const open = async (page) => { await page.locator('#koda-float').click({ button: 'right' }); await expect(menu(page)).toBeVisible(); };

test('the menu shows the model and level in use, a model chosen is an override with a chip and a stronger look, and Use default removes it', async ({ page }) => {
  await open(page);
  await expect(menu(page)).toContainText('Model and thinking');
  await expect(menu(page)).toContainText('GPT-5 Codex');
  await expect(menu(page)).toContainText('default');
  await expect(page.locator('#koda-layer .kmodelchip')).toBeHidden();
  const before = await page.locator('#koda-layer .pet').getAttribute('data-power');
  await menu(page).getByText('Model ›').hover();
  await menu(page).getByText('GPT-6 Pro').click();
  await expect(page.locator('#koda-layer .kmodelchip')).toBeVisible();
  await expect(page.locator('#koda-layer .kmodelchip')).toContainText('GPT-6 Pro');
  const after = await page.locator('#koda-layer .pet').getAttribute('data-power');
  expect(Number(after)).toBeGreaterThan(Number(before));
  await open(page);
  await expect(menu(page)).toContainText('only this session');
  await menu(page).getByText('Use default').click();
  await expect(page.locator('#koda-layer .kmodelchip')).toBeHidden();
  expect(await page.locator('#koda-layer .pet').getAttribute('data-power')).toBe(before);
});

test('the thinking level only offers what the model supports, a model that cannot do it drops it, and more thinking makes Koda look stronger', async ({ page }) => {
  await open(page);
  await menu(page).getByText('Model ›').hover();
  await menu(page).getByText('GPT-6 Pro').click();
  await open(page);
  await menu(page).getByText(/^Thinking/).hover();
  await menu(page).getByText('Extra high').click();
  await expect(page.locator('#koda-layer .kmodelchip')).toContainText('Extra high');
  expect(await page.locator('#koda-layer .pet').getAttribute('data-power')).toBe('4');
  await open(page);
  await menu(page).getByText('Model ›').hover();
  await menu(page).getByText('GPT-5 Codex mini').click();   // this model has no extra high: the level goes back to the default
  await expect(page.locator('#koda-layer .kmodelchip')).not.toContainText('Extra high');
  await open(page);
  await menu(page).getByText(/^Thinking/).hover();
  await expect(menu(page)).not.toContainText('Extra high');
});

test('Set as default for Koda saves the choice for the project, and there is no override left', async ({ page }) => {
  await open(page);
  await menu(page).getByText('Model ›').hover();
  await menu(page).getByText('GPT-6 Pro').click();
  await open(page);
  await menu(page).getByText('Set as default for Koda').click();
  await expect(page.locator('#koda-layer .kmodelchip')).toBeHidden();
  const saved = await page.evaluate(async () => (await import('/app/api.js')).api.get('/api/project/settings'));
  expect(saved.koda.model).toBe('gpt-6-pro');
  await page.evaluate(async () => (await import('/app/api.js')).api.post('/api/project/settings', { koda: { model: '', effort: '' } }));   // leave the project as it was
});

test('while Koda works the menu says a change counts from the next turn', async ({ page }) => {
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__applied = [];
    setBackend({ explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }), applyModel: async (model, effort) => { (window as any).__applied.push([model, effort]); }, run: () => new Promise(() => {}) });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('put a reverb after it');
  await page.keyboard.press('Enter');
  await expect(page.locator('#koda-layer .bub.k-thought')).toBeVisible();
  await open(page);
  await expect(menu(page)).toContainText('a change counts from the next turn');
  await menu(page).getByText('Model ›').hover();
  await menu(page).getByText('GPT-6 Pro').click();
  await expect.poll(() => page.evaluate(() => (window as any).__applied)).toEqual([['gpt-6-pro', '']]);
});
