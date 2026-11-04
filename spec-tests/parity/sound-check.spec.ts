import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// The sound check dialog, the meter row in the node and the impulse-response option of the convolution block.
const SONG = `composition "demo" { ample 1  tempo 92  master-seed 1 }
drums beats at 1 for 2 {
}
volume quiet {
  gain -38
  from beats.audio -> audio
}
meter stats {
  from quiet.audio -> audio
}
master master {
  from stats.audio -> audio
}
`;
freshServer({ song: SONG });

const built = async () => {
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await expect.poll(async () => (await api('/api/build/status?path=song.amplet')).blocks.every((b: { status: string }) => b.status === 'cached' || b.status === 'rendered'), { timeout: 60000 }).toBe(true);
};

test('the sound check names a very quiet block and a click puts a normalize block after it', async ({ page }) => {
  await openApp(page, { reset: false });
  await built();
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await page.getByRole('button', { name: 'Sound check', exact: true }).click();
  const dialog = page.locator('dialog.soundcheck');
  await expect(dialog).toBeVisible();
  const row = dialog.locator('.soundcheck-row', { hasText: 'quiet' }).first();
  await expect(row).toContainText(/very quiet|silent/);
  const fix = dialog.getByRole('button', { name: /Add normalize/ }).first();
  await expect(fix).toBeVisible();
  await fix.click();
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text ?? '', { timeout: 15000 }).toContain('normalize');
});

test('a meter shows its measurement in the node and the convolution block offers the ready-made impulse responses', async ({ page }) => {
  await openApp(page, { reset: false });
  await built();
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await expect(page.locator('.node[data-id="stats"] .meter-report')).toContainText('peak', { timeout: 15000 });
  const templates = await api('/api/templates');
  const list = (templates.templates ?? templates) as { id: string; parameters: { name: string; options?: string[] }[] }[];
  const ir = list.find((t) => t.id === 'convolution')?.parameters.find((p) => p.name === 'ir');
  expect(ir?.options).toEqual(expect.arrayContaining(['file', 'room', 'hall', 'plate', 'church', 'street']));
});
