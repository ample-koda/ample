import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The Storage page of the projects view: the overview, the dry run of a clean-up with its impact, a progress bar with cancel
// while it runs, and the options. The API answers are mocked so the page is tested without a takebank of gigabytes.
freshServer();

const overview = (state: string) => ({
  overview: {
    dataBytes: 12.4 * 1024 ** 3, takes: 2137, protectedBytes: 7.1 * 1024 ** 3, reclaimableBytes: 5.3 * 1024 ** 3, aiBytes: 1.7 * 1024 ** 3, tmpBytes: 300 * 1024 ** 2,
    historyBytes: 5 * 1024 ** 2, freeBytes: 10 * 1024 ** 3, totalBytes: 930 * 1024 ** 3, softLimitGb: 10, overLimit: true, lowDisk: false,
    byReason: [{ name: 'superseded', takes: 572, bytes: 8.6 * 1024 ** 3 }, { name: 'current', takes: 547, bytes: 7.5 * 1024 ** 3 }],
    byBlock: [], duplicateTakeRows: 0, dataFolder: 'C:\\project\\.ample\\data',
  },
  settings: { softLimitGb: 10, keepSuperseded: 0, keepMinutes: 60, dataPath: null, archivePath: null, intermediates: 'compressed', silenceRuns: true },
  clean: { state, done: 0, total: 0, bytes: 0, totalBytes: 0, error: null },
});

test('Storage page: overview, impact of a dry run, progress with cancel and the options', async ({ page }) => {
  let running = false;
  let polls = 0;
  await openApp(page);
  await page.route('**/api/storage', (route) => route.fulfill({ json: overview(running ? 'running' : 'idle') }));
  await page.route('**/api/storage/plan', (route) => route.fulfill({ json: { plan: { bytes: 5.3 * 1024 ** 3, tempBytes: 0, tempItems: 0, takes: 572, blobs: 600, protectedBytes: 7.1 * 1024 ** 3, freeBefore: 10 * 1024 ** 3, freeAfter: 15.3 * 1024 ** 3, fingerprint: 'abc123', byReason: [{ name: 'superseded', takes: 572, bytes: 5.3 * 1024 ** 3 }], byBlock: [{ name: 'melody', takes: 40, bytes: 1024 ** 3 }] } } }));
  await page.route('**/api/storage/clean', async (route) => { await new Promise((r) => setTimeout(r, 600)); running = true; return route.fulfill({ json: { started: true } }); });
  await page.route('**/api/storage/status', (route) => {
    polls += 1;
    if (!running) return route.fulfill({ json: { state: 'cancelled', done: polls, total: 10, bytes: polls * 1024 ** 3, totalBytes: 5.3 * 1024 ** 3, error: null } });
    return route.fulfill({ json: { state: 'running', done: polls, total: 10, bytes: polls * 1024 ** 3, totalBytes: 5.3 * 1024 ** 3, error: null } });
  });
  await page.route('**/api/storage/cancel', (route) => { running = false; return route.fulfill({ json: { state: 'cancelled' } }); });
  await page.route('**/api/storage/settings', (route) => route.fulfill({ json: { settings: {} } }));

  await page.evaluate(() => import('/app/projects.js').then((m) => m.openProjects('storage')));
  await expect(page.locator('#st-summary')).toContainText('12.4 GB in 2137 take(s)');
  await expect(page.locator('#st-over')).toContainText('over its soft limit of 10 GB');
  await expect(page.locator('#st-reasons')).toContainText('Older takes of a block that has a newer one');

  // dry run: impact first, nothing happens yet
  await page.locator('#st-dry').click();
  // the view opens at once, grey squares first, and shows block by block what it found
  await expect(page.locator('dialog#st-defrag')).toBeVisible();
  await expect(page.locator('#st-defrag .defrag-grid .dc').first()).toBeVisible();
  await expect(page.locator('#st-run')).toBeVisible({ timeout: 15000 });
  expect(await page.locator('#st-defrag .dc-u').count()).toBe(0);
  await expect(page.locator('#st-impact-line')).toContainText('Gives back 5.3 GB');
  await expect(page.locator('#st-impact')).toContainText('572');
  await expect(page.locator('#st-run')).toContainText('Clean up 5.3 GB');
  // it is a modal map of the take data: protected squares stay, the others go
  await expect(page.locator('dialog#st-defrag')).toBeVisible();
  expect(await page.locator('#st-defrag .defrag-grid .dc-p').count()).toBeGreaterThan(0);
  expect(await page.locator('#st-defrag .defrag-grid .dc-r').count()).toBeGreaterThan(0);
  await expect(page.locator('#st-defrag .defrag-grid')).toHaveAttribute('aria-label', /stays.*can be given back/);

  // run: a progress bar and a cancel button
  await page.locator('#st-run').click();
  // the moment it is clicked the button goes off and the view shows it is starting up
  await expect(page.locator('#st-run')).toBeDisabled();
  await expect(page.locator('#st-run')).toHaveText('Starting...');
  await expect(page.locator('#st-defrag')).toHaveClass(/starting/);
  await expect(page.locator('#st-progress')).toBeVisible();
  await expect(page.locator('#st-running')).toContainText('Cleaning up');
  await expect.poll(() => page.locator('#st-defrag .dc-gone').count()).toBeGreaterThan(0);   // squares turn into free space while it runs
  await page.locator('#st-cancel').click();
  await expect(page.locator('#st-running')).toContainText('Stopped after');
  await page.locator('#st-close').click();
  await expect(page.locator('dialog#st-defrag')).toHaveCount(0);

  // the options can be saved
  await page.evaluate(() => import('/app/projects.js').then((m) => m.openProjects('storage')));
  await page.locator('#st-limit').fill('20');
  const saved = page.waitForRequest((r) => r.url().endsWith('/api/storage/settings') && r.method() === 'POST');
  await page.locator('#st-save').click();
  expect((await saved).postDataJSON().softLimitGb).toBe(20);
});
