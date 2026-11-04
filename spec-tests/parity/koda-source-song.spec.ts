import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// In the source view the name of the song above the editor is a thing Koda can be pointed at, like the breadcrumb of the blocks view: Koda then looks at the whole song.
freshServer();

test('Koda can be pointed at the name of the song in the source view, and then it looks at the whole song', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  await expect(page.locator('#se-title')).toHaveAttribute('data-koda-song', '');
  const found = await page.evaluate(async () => {
    const gaze = await import('/app/koda-gaze.js');
    const r = document.getElementById('se-title')!.getBoundingClientRect();
    const target = gaze.targetAt(r.left + 4, r.top + r.height / 2);
    return { kind: target?.kind, path: target?.path, selector: target?.selector };
  });
  expect(found.kind).toBe('song');
  expect(found.selector).toBe('#se-title[data-koda-song]');
  expect(String(found.path)).toMatch(/\.amplet$/);
});
