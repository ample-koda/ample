import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

freshServer();

test('a 401 without a problem code still warns that the session is stale', async ({ page }) => {
  await openApp(page);
  await page.route('**/api/doc/edit', (route) => route.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ title: 'Unauthorized' }) }));
  await page.evaluate(async () => {
    const { api } = await import('/app/api.js');
    try { await api.post('/api/doc/edit', {}); } catch { /* expected unauthorized response */ }
  });
  await expect(page.locator('#session-warning')).toHaveJSProperty('open', true);
});
