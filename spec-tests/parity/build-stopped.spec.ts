import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// A build that was stopped (by an edit, an undo) may still send events from the server; they must not touch the status of the build that follows.
freshServer();
test('the events of a stopped build are ignored', async ({ page }) => {
  await openApp(page);
  let id = '';
  await page.route('**/api/build', async (route) => { id = JSON.parse(route.request().postData() || '{}').buildId || ''; /* never answered: the build hangs until it is stopped */ });
  await page.evaluate(async () => { const s = await import('/app/store.js'); void s.build(null, null, 'preview'); });
  await expect.poll(() => id).not.toBe('');
  const result = await page.evaluate(async (stopped) => {
    const s = await import('/app/store.js');
    s.stopBuild();
    await new Promise((r) => setTimeout(r, 100));
    s.onBuildProgress({ block: 'old-block', status: 'failed', buildId: stopped });
    s.onBuildProgress({ block: 'new-block', status: 'rendered', buildId: 'another' });
    return { old: s.S.build.status['old-block'] ?? null, now: s.S.build.status['new-block'] ?? null };
  }, id);
  expect(result.old).toBeNull();
  expect(result.now).not.toBeNull();
});
