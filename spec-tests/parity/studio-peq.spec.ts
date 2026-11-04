import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('the parametric EQ panel has a draggable handle per band and moving it writes the band', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'peq', 'peq');
  const node = page.locator('.node[data-id="peq"]');
  await node.getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  await expect(dialog.locator('.studio-peq-panel .studio-response-graph')).toBeVisible();
  for (let band = 1; band <= 6; band++) await expect(dialog.getByRole('slider', { name: `Band ${band}: frequency and gain` })).toBeVisible();
  await expect(dialog.locator('.studio-peq-band')).toHaveCount(6);
  // the keyboard moves a handle: up raises the gain of band 3 and the song text follows
  const handle = dialog.getByRole('slider', { name: 'Band 3: frequency and gain' });
  await handle.focus();
  await page.keyboard.press('ArrowUp');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toMatch(/band3_gain 0\.5 dB/);
});
