import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('distortion, delay and reverb show their effect plots', async ({ page }) => {
  await openApp(page, { reset: false });
  for (const [type, plotName] of [['Distortion', 'Distortion input and output transfer curve'], ['Delay (echo)', 'Delay echo tap levels'], ['Reverb', 'Reverb room and decay tail estimate']]) {
    const id = type === 'Distortion' ? 'distortion' : type === 'Delay (echo)' ? 'delay' : 'reverb';
    await addTemplateBlock(page, id, id);
    const node = page.locator(`.node[data-id="${id}"]`);
    await node.getByRole('button', { name: 'Advanced', exact: true }).click();
    await expect(page.locator('#studio').getByRole('img', { name: plotName })).toBeVisible();
    await page.keyboard.press('Escape');
  }
});
