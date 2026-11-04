import { test, expect } from '@playwright/test';
import { api, bottomTab, freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); await bottomTab(page, 'var'); });

async function make(page, count = '2') {
  await page.locator('#v-target').selectOption('master');
  await page.locator('.variant-field').filter({ hasText: 'target' }).locator('input').check();
  await page.locator('#v-count').selectOption(count);
  await expect(page.locator('.variant-proposal .note')).toHaveCount(Number(count));
  await page.click('#v-make');
  await expect(page.locator('#vgrid .card')).toHaveCount(Number(count), { timeout: 20000 });
}

test('making variants shows takes with waveforms', async ({ page }) => {
  await make(page);
  await expect(page.locator('#v-status')).toContainText('takes built');
  const drawn = await page.evaluate(() => new Promise<boolean>((res) => setTimeout(() => { const c = document.querySelector('#vgrid canvas') as HTMLCanvasElement; res(c.getContext('2d')!.getImageData(0, 0, c.width, c.height).data.some((v) => v !== 0)); }, 800)));
  expect(drawn).toBe(true);
});

test('takes appear one at a time and cancelling stops only the active take', async ({ page }) => {
  await page.locator('#v-target').selectOption('master');
  await page.locator('.variant-field').filter({ hasText: 'target' }).locator('input').check();
  await page.locator('#v-count').selectOption('2');
  let resume!: () => void, secondSeen!: () => void;
  const gate = new Promise<void>((resolve) => { resume = resolve; });
  const waiting = new Promise<void>((resolve) => { secondSeen = resolve; });
  let requests = 0;
  await page.route('**/api/control/variants', async (route) => {
    if (++requests === 2) { secondSeen(); await gate; try { await route.continue(); } catch { /* the UI cancelled this request */ } }
    else await route.continue();
  });
  await page.click('#v-make');
  await waiting;
  await expect(page.locator('#vgrid .card')).toHaveCount(1);
  await page.click('#v-cancel-current');
  await expect(page.locator('#v-status')).toContainText('Stopped');
  resume();
  await expect(page.locator('#vgrid .card')).toHaveCount(1);
  await page.unroute('**/api/control/variants');
  const variants = await api('/api/control/block?path=song.amplet&block=master');
  expect(variants.variants).toHaveLength(1);
});

test('asking again gives the same variants (seeds are derived)', async ({ page }) => {
  await make(page);
  const before = await page.locator('#vgrid .card').evaluateAll((e) => e.map((x) => (x as HTMLElement).dataset.take));
  await page.click('#v-make');
  await expect(page.locator('#vgrid .card')).toHaveCount(2);
  const after = await page.locator('#vgrid .card').evaluateAll((e) => e.map((x) => (x as HTMLElement).dataset.take));
  expect(after).toEqual(before);
});

test('auditioning an existing candidate plays its stored take without building audio', async ({ page }) => {
  await make(page);
  let builds = 0;
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds++;
  });
  await page.locator('#vgrid .card').first().locator('button', { hasText: '▶' }).click();
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing)).toBe(true);
  expect(builds).toBe(0);
  await page.click('#btn-stop');
});

test('apply and pick writes the exact variant fields and selects its take', async ({ page }) => {
  await make(page);
  const cards = page.locator('#vgrid .card');
  const first = (await cards.nth(0).getAttribute('data-take'))!;
  const displayed = (await cards.nth(0).locator('.variant-differences').textContent())!;
  const targetValue = displayed.match(/target: [^→]+→\s*([^·]+)/)?.[1].trim();
  expect(targetValue).toBeTruthy();
  await cards.nth(0).locator('button', { hasText: 'apply & pick' }).click();
  await expect(cards.nth(0)).toHaveClass(/picked/);
  expect((await api('/api/control/block?path=song.amplet&block=master')).selected).toBe(first);
  expect((await api('/api/doc?path=song.amplet')).text).toContain(`target ${targetValue}`);
  await cards.nth(1).locator('button', { hasText: 'keep' }).click();
  await expect(cards.nth(1).locator('button', { hasText: 'kept' })).toBeVisible();
});

test('discard removes a variant from the active list but leaves its tombstone auditable', async ({ page }) => {
  await make(page);
  const cards = page.locator('#vgrid .card');
  const take = (await cards.first().getAttribute('data-take'))!;
  await cards.first().locator('button', { hasText: 'apply & pick' }).click();
  await expect(cards.first()).toHaveClass(/picked/);
  await cards.nth(1).locator('button', { hasText: 'apply & pick' }).click();
  await expect(cards.nth(1)).toHaveClass(/picked/);
  await cards.first().locator('button', { hasText: 'discard' }).click();
  await expect(page.locator('#vgrid .card')).toHaveCount(1);
  const all = await api('/api/control/block?path=song.amplet&block=master');
  expect(all.variants.some((v: any) => v.takeId === take && v.state === 'discarded')).toBe(true);
});

test('field choices are previewed before rendering and recorded in the take plan', async ({ page }) => {
  await page.locator('#v-target').selectOption('master');
  await page.locator('.variant-field').filter({ hasText: 'target' }).locator('input').check();
  await expect(page.locator('.variant-proposal .note').first()).toContainText('target');
  const proposed = await page.locator('.variant-proposal .note').first().innerText();
  await page.locator('#v-count').selectOption('2');
  await page.click('#v-make');
  await expect(page.locator('#vgrid .card')).toHaveCount(2, { timeout: 20000 });
  await expect(page.locator('#vgrid .card').first().locator('.variant-differences')).toContainText('target');
  const take = (await page.locator('#vgrid .card').first().getAttribute('data-take'))!;
  const record = await api(`/api/takes/${encodeURIComponent(take)}`);
  const change = record.plan.settings.variantChanges.find((item: any) => item.name === 'target');
  expect(change.to).toBe(proposed.split('→').at(-1)?.trim());
});

test('making variants without selected fields refuses before rendering', async ({ page }) => {
  await page.locator('#v-target').selectOption('master');
  await page.click('#v-make');
  await expect(page.locator('#v-status')).toContainText('Select at least one field');
  await expect(page.locator('#vgrid .card')).toHaveCount(0);
});

test('scores are yours and stay after a reload', async ({ page }) => {
  await make(page);
  const score = page.locator('#vgrid .card').first().locator('input[type=number]');
  await score.fill('77');
  await score.press('Tab');
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await bottomTab(page, 'var');
  await expect(page.locator('#vgrid .card').first().locator('input[type=number]')).toHaveValue('77');
});

test('A/B compare plays both takes in sync and switches without a jump', async ({ page }) => {
  await make(page);
  const cards = page.locator('#vgrid .card');
  await cards.nth(0).locator('button', { hasText: /^A$/ }).click();
  await cards.nth(1).locator('button', { hasText: /^B$/ }).click();
  await expect(page.locator('#ab-bar')).toBeVisible();
  await page.click('#ab-play');
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_AB.state().playing)).toBe(true);
  await page.waitForTimeout(800);
  expect(await page.evaluate(() => (window as any).AMPLE_AB.drift())).toBeLessThan(0.1);
  const before = await page.evaluate(() => (window as any).AMPLE_AB.state().volumes);
  await page.click('#ab-switch');
  const after = await page.evaluate(() => (window as any).AMPLE_AB.state().volumes);
  expect(after[0]).not.toBe(before[0]);
  expect(await page.evaluate(() => (window as any).AMPLE_AB.drift())).toBeLessThan(0.1);
});
