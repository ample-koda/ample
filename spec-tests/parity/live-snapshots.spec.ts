import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();

// Local preview 6.2: snapshots A to D of a block's live parameters, recall, and a morph between two that is written once on release.
test('@audio store two snapshots of an EQ, recall one, morph halfway', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'eq', 'tone');
  const setHigh = async (value: string) => {
    const doc = await api('/api/doc?path=song.amplet');
    await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'setParameter', block: 'tone', name: 'high', value } } });
  };
  await setHigh('-12 dB');
  await page.reload(); await page.waitForSelector('body[data-ready="1"]');
  await page.locator('.node[data-id="tone"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const bar = page.locator('#studio .snapshot-bar');
  await expect(bar).toBeVisible();
  const high = () => page.evaluate(() => import('/app/store.js').then((s) => s.flat().nodes.find((n) => n.id === 'tone')?.params?.high));
  await bar.locator('.snap-slot[data-slot="A"]').click();   // store -12 dB as A
  await expect(bar.locator('.snap-slot[data-slot="A"]')).toHaveClass(/filled/);
  await page.evaluate(() => import('/app/store.js').then(async (s) => { await s.edit([{ type: 'setParameter', block: 'tone', name: 'high', value: '0 dB' }]); }));
  await expect.poll(high).toBe('0 dB');
  await expect(bar).toBeVisible();
  await bar.locator('.snap-slot[data-slot="B"]').click();   // store 0 dB as B
  await expect(bar.locator('.snap-slot[data-slot="B"]')).toHaveClass(/filled/);
  await bar.locator('.snap-slot[data-slot="A"]').click();   // recall A
  await expect.poll(high).toBe('-12 dB');
  const before = await page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version));
  await expect(bar).toBeVisible();
  await bar.locator('.snap-morph').fill('0.5');
  await expect.poll(high).toBe('-6 dB');
  expect(await page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version))).toBeGreaterThan(before);
});

test('@audio snapshots and old browser-only ones live in the project', async ({ page }) => {
  await page.addInitScript(() => { if (!localStorage.getItem('__seeded')) { localStorage.setItem('__seeded', '1'); localStorage.setItem('ample.snapshots', JSON.stringify({ 'x#y': { A: { high: '3 dB' } } })); localStorage.setItem('ample.midi.learn', JSON.stringify({ '1:7': { block: 'b', name: 'n' } })); } });
  await openApp(page, { reset: false });
  await expect.poll(() => page.evaluate(() => import('/app/ui-state.js').then((m) => m.loadUiState().then((s) => JSON.stringify(s))))).toContain('"1:7"');
  await page.evaluate(() => import('/app/ui-state.js').then((m) => m.flushUiState()));
  expect(await page.evaluate(() => localStorage.getItem('ample.snapshots'))).toBe(null);   // moved, not copied
  const saved = await page.evaluate(() => fetch('/api/project/ui-state', { headers: { 'X-Ample-Token': (window as any).AMPLE_TOKEN || '' } }).then((r) => r.status));
  expect([200, 401]).toContain(saved);
  await page.evaluate(() => import('/app/api.js').then((a) => a.api.get('/api/project/ui-state'))).then((s) => expect(s.snapshots['x#y'].A.high).toBe('3 dB'));
});
