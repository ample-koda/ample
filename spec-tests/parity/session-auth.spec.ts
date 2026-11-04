import { test, expect } from '@playwright/test';
import { openApp, launchUrl } from '../../web/tests/support';

test('a rejected API session shows recovery instructions', async ({ page }) => {
  await openApp(page);
  await page.evaluate(() => document.querySelector('#dlg').showModal());
  await page.route('**/api/synthetic-unauthorized', route => route.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ code: 'AMP5284', detail: 'session rejected' }) }));
  await page.evaluate(async () => {
    const { api } = await import('/app/api.js');
    try { await api.get('/api/synthetic-unauthorized'); } catch { /* the alert is the behavior under test */ }
  });
  await expect(page.locator('#session-warning')).toBeVisible();
  await expect(page.locator('#session-warning')).toHaveJSProperty('open', true);
  expect(await page.locator('#session-warning').evaluate((element) => element.matches(':modal'))).toBe(true);
  await expect(page.locator('#session-warning')).toContainText('ample open');
});

test('an expired AI task token does not mark the Studio session as invalid', async ({ page }) => {
  await openApp(page);
  await page.route('**/api/synthetic-expired-task', route => route.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ code: 'AMP5458', detail: 'task ended' }) }));
  await page.evaluate(async () => {
    const { api } = await import('/app/api.js');
    try { await api.get('/api/synthetic-expired-task'); } catch { /* the warning state is the behavior under test */ }
  });
  await expect(page.locator('#session-warning')).toBeHidden();
});

test('a rejected event-stream session is checked once without an endless reconnect loop', async ({ page }) => {
  await page.goto(new URL('/app/api.js', launchUrl()).toString());
  const result = await page.evaluate(async () => {
    const instances: any[] = [];
    window.EventSource = class {
      onerror: (() => void) | null = null;
      constructor() { instances.push(this); }
      addEventListener() {}
      close() {}
    } as any;
    window.fetch = async () => new Response(JSON.stringify({ code: 'AMP5284', detail: 'session rejected' }), { status: 401 });
    let lost = 0;
    document.addEventListener('ample:session-lost', () => lost++);
    const { connectEvents } = await import('/app/api.js');
    const stop = connectEvents({});
    instances[0].onerror();
    await new Promise(resolve => setTimeout(resolve, 100));
    stop();
    return { opened: instances.length, lost };
  });
  expect(result).toEqual({ opened: 1, lost: 1 });
});
