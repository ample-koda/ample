import { test, expect } from '@playwright/test';
import { freshServer, openApp, node } from '../../web/tests/support';

freshServer({ song: `composition "demo" { ample 1  tempo 92 master-seed 1 }
bass builtin {
  set analog-808
}
bass sampled {
  set samples/bass
}
` });

test('bass sample-set fields show the built-in placeholder or the chosen folder path', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'builtin').locator('.nh .t').click();
  await page.locator('#props [role="tab"][data-key="parameters"]').click();
  const builtIn = page.locator('#props .prop-tabpanel.active .prop', { hasText: 'set' }).locator('input');
  await expect(builtIn).toHaveAttribute('placeholder', 'built-in synth (choose a folder for samples)');
  await expect(builtIn).toHaveValue('');

  await node(page, 'sampled').locator('.nh .t').click();
  const folder = page.locator('#props .prop-tabpanel.active .prop', { hasText: 'set' }).locator('input');
  await expect(folder).toHaveValue('samples/bass');
});
