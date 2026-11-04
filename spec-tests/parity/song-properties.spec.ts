import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

freshServer();

test('empty canvas shows Song properties and the seed override follows the project seed when cleared', async ({ page }) => {
  await openApp(page);
  await node(page, 'master').click({ position: { x: 20, y: 36 } });
  await expect(page.locator('#props > h4').first()).toContainText('Block properties');
  await page.locator('#cv').click({ position: { x: 5, y: 5 } });
  await expect(page.locator('#props > h4').first()).toHaveText('Song properties');

  const input = page.getByRole('textbox', { name: 'Song seed override' });
  const projectSeed = String((await api('/api/project')).masterSeed);
  const songSeed = /master-seed (\d+)/.exec((await api('/api/doc?path=song.amplet')).text)![1];
  await expect(input).toHaveValue(songSeed === projectSeed ? '' : songSeed);
  await expect(input).toHaveAttribute('placeholder', `Project seed (${projectSeed})`);

  await input.fill('12345');
  await input.press('Tab');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text).toContain('master-seed 12345');
  await expect(input).toHaveValue('12345');

  await input.fill('');
  await input.press('Tab');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text).toContain(`master-seed ${projectSeed}`);
  await expect(input).toHaveValue('');
});
