import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

const song = fs.readFileSync(path.resolve('docs/clientside-preview/demo/advanced/sequencer.amplet'), 'utf8');
freshServer({ song });

test('embedded built-in sequencer channels and named lanes preview locally', async ({ page }) => {
  await openApp(page, { reset: false });
  const support = await page.evaluate(async () => {
    const { flat } = await import('/app/store.js');
    const { sequencePreviewPlan } = await import('/lib/clientside-sequencer.mjs');
    const node = flat().nodes.find((item: any) => item.id === 'sq');
    return { tpl: node?.tpl, parameterNames: Object.keys(node?.params || {}), plan: node ? !!sequencePreviewPlan(node.params.sequence, node.params.lanes, 1, 8) : false };
  });
  expect(support).toMatchObject({ tpl: 'sequencer', plan: true });
  let builds = 0;
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds++;
  });

  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  await expect(page.locator('#toast')).toContainText('Local instrument approximation: sq');
  expect(builds).toBe(0);
  await page.click('#btn-stop');
});
