import { test, expect } from '@playwright/test';
import { launchUrl } from '../../web/tests/support';

// Every sign-in made a cookie with a new name and none expired: the Cookie header grew until nginx answered the language-server WebSocket (HTTP/1.1) with a 400.
// The page keeps its own cookie and the newest few of the others, and the cookie now expires.
test('a pile of old token cookies is cleaned when the page opens, the cookie of the session stays and expires', async ({ page, context }) => {
  const url = new URL(launchUrl());
  const old = Array.from({ length: 30 }, (_, i) => ({ name: `ample-token-${50000 + i}-${String(i).padStart(16, '0')}`, value: 'x'.repeat(64), domain: url.hostname, path: '/' }));
  await context.addCookies(old);
  await page.goto(launchUrl());
  await page.waitForSelector('body[data-ready="1"]');
  const names = (await context.cookies()).map((c) => c.name).filter((n) => n.startsWith('ample-token-'));
  expect(names.length).toBeLessThanOrEqual(5);   // its own and at most four of the others
  const own = (await context.cookies()).find((c) => c.name.startsWith(`ample-token-${url.port}-`) && c.value.length > 0 && !c.name.includes('0000000000000'));
  expect(own, 'the cookie of this session').toBeTruthy();
  expect(own!.expires).toBeGreaterThan(Date.now() / 1000 + 3 * 86400);   // it expires (a week), it is no longer a cookie that stays for ever
});
