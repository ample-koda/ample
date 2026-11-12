import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// The light next to the title says what it does, a bus and a mixer say which one they are, and the Patterns view of the drums is the main button of its panel.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

async function open(page, type: string) {
  await addTemplateBlock(page, type, `l-${type}`);
  await node(page, `l-${type}`).getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  await expect(dialog).toBeVisible();
  return dialog;
}

test('the light next to the title has a tooltip that says it switches the block on or off', async ({ page }) => {
  const dialog = await open(page, 'delay');
  const light = dialog.getByRole('switch', { name: 'Effect enabled' });
  await expect(light).toHaveAttribute('title', /switch the block off/);
});

test('a bus and a mixer say which one they are', async ({ page }) => {
  const bus = await open(page, 'bus');
  await expect(bus.locator('.studio-mixer-kind')).toContainText('Bus');
  await expect(bus.locator('.studio-bus-panel')).toHaveCount(1);
  await page.keyboard.press('Escape');
  await expect(bus).toBeHidden();
  const mixer = await open(page, 'mixer');
  await expect(mixer.locator('.studio-mixer-kind')).toContainText('Mixer');
  await expect(mixer.locator('.studio-bus-panel')).toHaveCount(0);
});

test('the Patterns view of the drums is the primary button of the step grid', async ({ page }) => {
  await node(page, 'drums').locator('.nh .t').click();
  await expect(page.locator('#seq-drums')).toBeVisible();
  await expect(page.locator('#seq-open-view')).toHaveClass(/primary/);
});
