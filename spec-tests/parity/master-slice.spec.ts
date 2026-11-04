import { test, expect } from '@playwright/test';
import { bottomTab, openApp, state } from '../../web/tests/support';

// The master slice: the part of the song that comes out of the master, chosen in the Arrangement with the bars of the ruler.
test.beforeEach(async ({ page }) => { await openApp(page); await bottomTab(page, 'arr'); await page.evaluate(() => document.querySelector<HTMLElement>('#bottom')!.style.setProperty('--bh', '560px')); });

test('the master slice is set from the chosen bars, shows as a band with edges that can be dragged, and can be cleared', async ({ page }) => {
  await page.evaluate(async () => { const store = await import('/app/store.js'); if (!store.flat().nodes.some((n: { tpl: string }) => n.tpl === 'master')) await store.edit([{ type: 'addBlock', blockType: 'master', name: 'master' }]); });
  const ruler = page.locator('#arr .ruler');
  const box = (await ruler.boundingBox())!;
  const bw = Number((await page.locator('#arr-bw').textContent())!.replace(/\D/g, ''));
  // choose bars 3 and 4 on the ruler
  await page.mouse.move(box.x + 2 * bw + 4, box.y + 10);
  await page.mouse.down(); await page.mouse.move(box.x + 4 * bw - 4, box.y + 10, { steps: 5 }); await page.mouse.up();
  await page.locator('#arr-slice-set').click();
  await expect.poll(async () => (await state(page)).text).toMatch(/slice_from 3[\s\S]*slice_to 4|slice_to 4[\s\S]*slice_from 3/);
  const band = page.locator('#arr-slice');
  await expect(band).toBeVisible();
  // the right edge dragged one bar further
  const edge = (await band.locator('.arr-slice-edge.right').boundingBox())!;
  await page.mouse.move(edge.x + edge.width / 2, edge.y + edge.height / 2);
  await page.mouse.down(); await page.mouse.move(edge.x + edge.width / 2 + bw, edge.y + edge.height / 2, { steps: 5 }); await page.mouse.up();
  await expect.poll(async () => (await state(page)).text).toContain('slice_to 5');
  await page.locator('#arr-slice-clear').click();
  await expect.poll(async () => (await state(page)).text).not.toContain('slice_from');
  await expect(page.locator('#arr-slice')).toHaveCount(0);
});
