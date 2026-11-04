import { test, expect } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { api, freshServerPerTest, openApp, ownServer } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

freshServerPerTest();

test('the last opened song survives reload and recent-project updates', async ({ page }) => {
  await openApp(page, { reset: false });
  await writeFile(join(ownServer()!.folder, 'second.amplet'), demoSong());
  await page.evaluate(() => import('/app/store.js').then((store) => store.openDoc('second.amplet')));
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_STATE.path)).toBe('second.amplet');
  expect((await api('/api/project')).lastSong).toBe('second.amplet');
  await api('/api/projects');
  await page.evaluate(() => localStorage.clear());
  await page.reload(); await page.waitForSelector('body[data-ready="1"]');
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_STATE.path)).toBe('second.amplet');
});
