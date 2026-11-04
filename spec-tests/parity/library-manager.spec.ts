import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('the library view lists the assets of a linked folder with a details panel and a lock line', async ({ page }) => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ample-lib-'));
  fs.writeFileSync(path.join(dir, 'kick.wav'), Buffer.from('RIFF0000WAVEfmt '));
  await api('/api/library/mounts', { method: 'POST', body: { name: 'kit', path: dir, readOnly: true, scope: 'global' } });
  await openApp(page, { reset: false });
  await page.click('#vs-library');
  await expect(page.locator('#lb')).toBeVisible();
  const row = page.locator('.lbrow[data-asset]', { hasText: 'kick' });
  await expect(row).toBeVisible();
  await row.click();
  await expect(page.locator('#lb-import')).toBeVisible();
  await expect(page.locator('#lb-lock')).toContainText('ample.lock');
  await page.click('#vs-blocks');
  await expect(page.locator('#lb')).toBeHidden();
  fs.rmSync(dir, { recursive: true, force: true });
});
