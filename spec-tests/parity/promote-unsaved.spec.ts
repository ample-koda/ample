import { test, expect } from '@playwright/test';
import { api, dialog, freshServer, openApp, state } from '../../web/tests/support';

// Making garage the main branch while studio is open must not lose what is not saved on studio (issue #151).
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); });

const studioHead = async () => {
  const lines = (await api('/api/history/graph')).lines as { name: string; head: string }[];
  return lines.find((l) => l.name === 'studio')!.head;
};

test('promoting while studio is open with unsaved edits asks first, and Cancel changes nothing', async ({ page }) => {
  await api('/api/history/go', { method: 'POST', body: { line: 'studio' } });
  await page.reload();
  await expect.poll(async () => (await api('/api/history/graph')).current).toBe('studio');
  await page.evaluate(() => import('/app/store.js').then((s) => s.applyText(s.S.doc.text + '\n# unsaved studio edit\n')));
  const before = await studioHead();

  const promoting = page.evaluate(() => import('/app/history.js').then((m) => m.promote()));
  await dialog(page, 'Make main');
  // the second dialog is the unsaved-work question of the branch switch (the first one says "not saved yet", not "changes that are not saved")
  await expect(page.locator('#dlg')).toContainText('changes that are not saved');
  await dialog(page, 'Cancel');
  await promoting;

  expect(await studioHead()).toBe(before);
  expect((await state(page)).text).toContain('# unsaved studio edit');
});
