import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

freshServer();
test('a question with three choices shows its buttons in one row', async ({ page }) => {
  await openApp(page);
  await page.evaluate(async () => { const { ask } = await import('/app/ui.js'); void ask('Three', 'Pick one', [{ label: 'Cancel', value: false }, { label: 'Add its own master', value: 'a' }, { label: 'Link to the master of the song', value: 'b', primary: true }]); });
  const tops = await page.locator('#dlg-btns button').evaluateAll((bs) => bs.map((b) => Math.round(b.getBoundingClientRect().top)));
  expect(new Set(tops).size).toBe(1);
  expect(tops.length).toBe(3);
});
