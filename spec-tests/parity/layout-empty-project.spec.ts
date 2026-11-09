import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// Without any open song there are no tabs: the tab bar is hidden and its grid row is free. The bottom panel
// (Arrangement, Branches, ...) must stay under the canvas and not move into that row.
test('the bottom panel stays below the canvas when the tab bar is hidden (no song, no tabs)', async ({ page }) => {
  await openApp(page);
  const tops = () => page.evaluate(() => ({
    canvas: Math.round(document.querySelector('#work')!.getBoundingClientRect().top),
    bottom: Math.round(document.querySelector('#bottom')!.getBoundingClientRect().top),
  }));
  const normal = await tops();
  expect(normal.bottom).toBeGreaterThan(normal.canvas);
  await page.evaluate(() => { (document.querySelector('#tabbar') as HTMLElement).hidden = true; });
  const hidden = await tops();
  expect(hidden.bottom, JSON.stringify({ normal, hidden })).toBeGreaterThan(hidden.canvas);
});
