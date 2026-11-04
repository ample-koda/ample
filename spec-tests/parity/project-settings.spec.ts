import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

freshServer();

test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.click('#btn-projects'); await page.click('#pm [data-page="settings"]'); });

test('AMPLE Redux retention settings are saved with the project', async ({ page }) => {
  await expect(page.locator('#pm')).toContainText('AMPLE Redux budget (MiB)');
  await page.locator('#ps-budget').fill('64');
  await page.locator('#ps-full').fill('10');
  await page.click('#ps-save');
  await expect.poll(async () => (await api('/api/project/settings')).blackBox.budgetBytes).toBe(64 * 1048576);
  expect((await api('/api/project/settings')).blackBox.fullDetailDays).toBe(10);
});

test('the project can render fewer blocks at once than the server allows, and never more', async ({ page }) => {
  const before = await api('/api/project/settings');
  expect(before.parallelismLimit).toBeGreaterThanOrEqual(1);
  expect(before.parallelismLimit).toBeLessThanOrEqual(before.cores);
  expect(before.buildParallelism ?? null).toBeNull();
  await expect(page.locator('#ps-parallelism')).toHaveAttribute('placeholder', `server: ${before.parallelismLimit}`);
  await page.locator('#ps-parallelism').fill('1');
  await page.click('#ps-save');
  await expect.poll(async () => (await api('/api/project/settings')).buildParallelism).toBe(1);
  // more than the server allows is refused, and the stored number stays
  const refused = await api('/api/project/settings', { method: 'POST', body: { buildParallelism: before.parallelismLimit + 1 } });
  expect(JSON.stringify(refused)).toContain('AMP5941');
  expect((await api('/api/project/settings')).buildParallelism).toBe(1);
  // empty follows the server again
  await api('/api/project/settings', { method: 'POST', body: { buildParallelism: null } });
  expect((await api('/api/project/settings')).buildParallelism ?? null).toBeNull();
});

test('a value that makes no sense is refused', async () => {
  const r = await api('/api/project/settings', { method: 'POST', body: { blackBoxBudget: -5 } });
  expect(JSON.stringify(r)).toMatch(/AMP|error|title/i);
  expect((await api('/api/project/settings')).blackBox.budgetBytes).toBeGreaterThan(0);
});

test('AMPLE Redux status reports aggregate bytes and a warning threshold tied to the configured budget', async () => {
  await api('/api/project/settings', { method: 'POST', body: { blackBoxBudget: 100 * 1048576 } });
  const status = await api('/api/redux/status');
  expect(status.usedBytes).toBeGreaterThan(0);
  expect(status.warnAtBytes).toBe(20 * 1048576);
  expect(status.budgetBytes).toBe(100 * 1048576);
  expect(status.state).toMatch(/^(ok|warning|limit)$/);
});

test('AMPLE Redux compaction preview is read-only and reports the exact current byte count', async () => {
  const before = await api('/api/redux/status');
  const preview = await api('/api/redux/compact-preview');
  const after = await api('/api/redux/status');
  expect(preview.bytesBefore).toBe(before.usedBytes);
  expect(preview.bytesAfter).toBeLessThanOrEqual(preview.bytesBefore);
  expect(after.usedBytes).toBe(before.usedBytes);
});

test('the master seed is shown but only changes through the dangerous act', async ({ page }) => {
  await page.click('#ps-tabbtn-seed');
  await expect(page.locator('#ps-seed')).toBeDisabled();
});

test('the settings are split in tabs and each Save stores only its own tab', async ({ page }) => {
  await expect(page.locator('.ps-tabs [role="tab"]')).toHaveText(['General', 'Audio routing', 'Web search', 'Safe saves', 'Assistant', 'Seed and folders']);
  await expect(page.locator('#ps-tab-general')).toBeVisible();
  await expect(page.locator('#ps-tab-audio')).toBeHidden();
  const before = await api('/api/project/settings');
  // edit a field on two tabs but save only the audio tab: the general edit must not be stored
  await page.locator('#ps-budget').fill('77');
  await page.click('#ps-tabbtn-audio');
  await expect(page.locator('#ps-tab-audio')).toBeVisible();
  await page.locator('#ps-faer-song').selectOption('faer-else-local');
  await page.click('#ps-save-audio');
  await expect.poll(async () => (await api('/api/project/settings')).faer.song).toBe('faer-else-local');
  expect((await api('/api/project/settings')).blackBox.budgetBytes).toBe(before.blackBox.budgetBytes);
  // keyboard: arrow keys move between tabs
  await page.locator('#ps-tabbtn-audio').focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#ps-tab-search')).toBeVisible();
  await api('/api/project/settings', { method: 'POST', body: { faer: { song: 'local', voice: 'local', separation: 'local' } } });
});

test('the new master seed has a Random button, and the autosave slider shows its value and steps', async ({ page }) => {
  await page.click('#ps-tabbtn-seed');
  await page.locator('#ps-tab-seed .seed-dice').click();
  await expect(page.locator('#ps-newseed')).toHaveValue(/^\d{1,10}$/);
  await page.click('#ps-tabbtn-saves');
  await expect(page.locator('.ps-slider .ps-slider-line #ps-auto-minutes')).toBeVisible();
  await expect(page.locator('.ps-ticks span').first()).toHaveText(/min|h/);
});

// Plan engine-per-block: the project can name a default ACE model next to the routes; a model that is not a family or a .gguf file name is refused (AMP5383).
test('the default song and voice models are saved with the routes and a wrong model is refused', async ({ page }) => {
  await page.click('#ps-tabbtn-audio');
  await page.locator('#ps-faer-song-model').fill('xl-sft');
  await page.locator('#ps-faer-voice-model').fill('acestep-v15-turbo-Q8_0.gguf');
  await page.click('#ps-save-audio');
  await expect.poll(async () => (await api('/api/project/settings')).faer.songModel).toBe('xl-sft');
  expect((await api('/api/project/settings')).faer.voiceModel).toBe('acestep-v15-turbo-Q8_0.gguf');
  const refused = await api('/api/project/settings', { method: 'POST', body: { faer: { song: 'local', voice: 'local', separation: 'local', songModel: 'huge' } } });
  expect(JSON.stringify(refused)).toContain('AMP5383');
  expect((await api('/api/project/settings')).faer.songModel).toBe('xl-sft');
  // a request that does not mention the models keeps them; an empty text clears them
  await api('/api/project/settings', { method: 'POST', body: { faer: { song: 'local', voice: 'local', separation: 'local' } } });
  expect((await api('/api/project/settings')).faer.songModel).toBe('xl-sft');
  await api('/api/project/settings', { method: 'POST', body: { faer: { song: 'local', voice: 'local', separation: 'local', songModel: '', voiceModel: '' } } });
  const cleared = (await api('/api/project/settings')).faer;
  expect(cleared.songModel ?? '').toBe('');
  expect(cleared.voiceModel ?? '').toBe('');
});
