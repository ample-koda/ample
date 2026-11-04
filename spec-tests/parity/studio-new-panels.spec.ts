import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServer, openApp } from '../../web/tests/support';

// The panels of the effects added with the mix-and-record plan (X03, X04): each opens from the node, has controls, raises no page error, and a key press on a slider is written to the block's parameters.
freshServer();

const TYPES = ['limiter', 'pan', 'width', 'gate', 'deesser', 'chorus', 'flanger', 'phaser', 'bitcrusher'];
const paramsOf = async (name: string) => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === name).parameters.map((p: { name: string; value: string }) => `${p.name}=${p.value}`).join(' ');

test('the panels of the new effects open, show controls and write a changed slider into the song', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await openApp(page, { reset: false });
  for (const type of TYPES) await addTemplateBlock(page, type, `x-${type}`);
  for (const type of TYPES) {
    const node = page.locator(`.node[data-id="x-${type}"]`);
    await node.getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
    const dialog = page.locator('#studio[open]');
    await expect(dialog, type).toBeVisible();
    const sliders = dialog.getByRole('slider');
    expect(await sliders.count(), `${type} has sliders`).toBeGreaterThan(0);
    const before = await paramsOf(`x-${type}`);
    // the panel can still be drawing when the dialog opens and take the focus back: focus and press again until a key is written
    await expect.poll(async () => {
      await sliders.first().focus();
      await page.keyboard.press('ArrowUp');
      return paramsOf(`x-${type}`);
    }, { message: `${type}: a slider key writes a parameter`, timeout: 15000 }).not.toBe(before);
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
  }
  expect(errors).toEqual([]);
});
