import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('a linked folder shows in the side panel, read-only by default, and an offline one is marked', async ({ page }) => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ample-mount-'));
  fs.writeFileSync(path.join(dir, 'a.txt'), 'hello');
  const r = await api('/api/library/mounts', { method: 'POST', body: { name: 'samples', path: dir, readOnly: true, scope: 'global' } });
  expect(JSON.stringify(r)).toContain('samples');
  await openApp(page, { reset: false });
  const row = page.locator('#mounts .mrow', { hasText: 'samples' });
  await expect(row).toBeVisible();
  await expect(row).toContainText('🔒');
  fs.rmSync(dir, { recursive: true, force: true });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await expect(page.locator('#mounts .mrow.bad')).toBeVisible();
});
