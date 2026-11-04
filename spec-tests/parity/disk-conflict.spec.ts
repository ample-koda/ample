import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { api, freshServer, openApp, ownServer } from '../../web/tests/support';

// The file of the open song changes on disk while the working copy changes the same place: the save is refused and the dialog lets you choose per difference.
const song = `composition "Demo" { ample 1  tempo 100  master-seed 3 }
# one
# two
# three
`;

freshServer({ song });

test('a change on disk in the same place as the working copy is chosen per difference, not overwritten', async ({ page }) => {
  await openApp(page, { reset: false });
  const file = path.join(ownServer()!.folder, 'song.amplet');
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', baseVersion: doc.version, text: song.replace('# two', '# mine') } });
  fs.writeFileSync(file, song.replace('# two', '# theirs'));

  await page.locator('body').click({ position: { x: 5, y: 5 } });
  await page.keyboard.press('Control+s');   // the save shortcut of the Studio
  const dialog = page.locator('#dlg');
  await expect(dialog).toContainText('The file changed on disk');
  expect(fs.readFileSync(file, 'utf8')).toContain('# theirs');   // nothing was saved
  await expect(dialog.locator('.dc-region')).toHaveCount(1);
  await dialog.getByLabel('Both (A, then B)').check();
  await dialog.getByRole('button', { name: 'Use my choices' }).click();

  await page.keyboard.press('Control+s');
  await expect.poll(() => fs.readFileSync(file, 'utf8')).toBe(song.replace('# two', '# theirs\n# mine'));
});

test('a change on disk in another place is merged without asking', async ({ page }) => {
  await openApp(page, { reset: false });
  const file = path.join(ownServer()!.folder, 'song.amplet');
  fs.writeFileSync(file, song);
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', baseVersion: doc.version, text: song.replace('# one', '# ONE') } });
  fs.writeFileSync(file, song.replace('# three', '# THREE'));
  const merged = await api('/api/doc/disk?path=song.amplet');
  expect(['merged', 'same']).toContain(merged.state);   // the open page may have reloaded (and so merged) the song already
  const after = await api('/api/doc?path=song.amplet');
  expect(after.text).toContain('# ONE');
  expect(after.text).toContain('# THREE');
});
