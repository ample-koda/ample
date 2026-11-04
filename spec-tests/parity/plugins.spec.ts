import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

freshServer({ song: 'composition "demo" { ample 1  tempo 92  master-seed 1 }\ndrums beat {\n}\nscorer check {\n  from beat.audio -> audio\n}\n' });

test('the valid plugins are found with their kind and the panel lists them; there are no problems', async ({ page }) => {
  const r = await api('/api/plugins');
  expect(r.problems).toEqual([]);
  const ids = r.plugins.map((p) => p.id).sort();
  expect(ids).toEqual(expect.arrayContaining(['acestep', 'echo', 'flaky']));
  expect(r.plugins.find((p) => p.id === 'echo').kind).toBe('voice');
  expect(r.plugins.find((p) => p.id === 'analysis-fake').kind).toBe('analysis');
  await openApp(page, { reset: false });
  await expect(page.locator('#plugins .prow', { hasText: 'echo' })).toBeVisible();
});

test('a block whose plugin is not installed is blocked explicitly, never skipped silently', async ({ page }) => {
  await openApp(page, { reset: false });
  const diagnostics: any[] = await page.evaluate(() => (window as any).AMPLE_STATE.doc.diagnostics ?? []);
  expect(JSON.stringify(diagnostics)).toContain('AMP5001');
  await expect(page.locator('#btn-build')).toBeEnabled({ timeout: 15000 });
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  const status = await page.evaluate(() => (window as any).AMPLE_STATE.build.status.check);
  expect(status.status).toBe('skipped');
  expect(status.message).toContain('AMP5251');
  await expect(node(page, 'check')).toContainText(/skipped|plugin missing/);   // the node says it in its own words now
});
