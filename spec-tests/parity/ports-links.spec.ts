import { test, expect } from '@playwright/test';
import { node, openApp, state } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); });

const dot = (page, id: string, dir: 'in' | 'out', idx = 0) => node(page, id).locator(`.port.${dir}[data-idx="${idx}"]`);

async function drag(page, from, to) {
  const a = (await from.boundingBox())!, b = (await to.boundingBox())!;
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move((a.x + b.x) / 2, (a.y + b.y) / 2, { steps: 3 });
  await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 3 });
  return async () => { await page.mouse.up(); };
}

test('a compatible port connects and the source gets a from line', async ({ page }) => {
  const up = await drag(page, dot(page, 'chords', 'out'), dot(page, 'drums', 'in'));
  await up();
  await expect.poll(async () => (await state(page)).text).toMatch(/drums drums[^]*from chords\.notes -> trigger/);
});

test('while dragging, compatible inputs light up and the others dim', async ({ page }) => {
  const up = await drag(page, dot(page, 'chords', 'out'), dot(page, 'drums', 'in'));
  await expect(page.locator('.port.in.can').first()).toBeVisible();
  await expect(page.locator('.port.in.cannot').first()).toBeVisible();
  await up();
});

test('an incompatible port is refused with a message and nothing changes', async ({ page }) => {
  const before = (await state(page)).text;
  const up = await drag(page, dot(page, 'drums', 'out'), dot(page, 'bass', 'in'));
  await up();
  await expect(page.locator('#toast')).toContainText('Types differ');
  expect((await state(page)).text).toBe(before);
});

test('a link can be selected and deleted with the Delete key', async ({ page }) => {
  expect((await state(page)).text).toContain('from chords.notes -> notes');
  const hit = page.locator('#links path.hit').first();
  await hit.dispatchEvent('click');
  await page.keyboard.press('Delete');
  await expect.poll(async () => ((await state(page)).text.match(/from /g) || []).length).toBe(5);
});

test('undo brings a deleted link back', async ({ page }) => {
  const before = (await state(page)).text;
  await page.locator('#links path.hit').first().dispatchEvent('click');
  await page.keyboard.press('Delete');
  await expect.poll(async () => (await state(page)).text).not.toBe(before);
  await page.click('#btn-undo');
  await expect.poll(async () => (await state(page)).text).toBe(before);
});
