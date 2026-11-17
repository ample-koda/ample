import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// plan/diagnostics WP8: the song log has the toggles of the session layer. Assertions and the log level are set in the server
// session and in the browser evaluation in one step; the problems and the events of the song follow at once, and nothing is
// written to the song.
const song = 'composition "log" { ample 1  tempo 100  master-seed 1 }\n\n'
  + 'helper checked(Notes n) -> Notes {\n  log "checking"\n  assert 1 > 2, "never true"\n  return n\n}\n\n'
  + 'midi-clip melody at 1 for 1 {\n  notes "1:0:1:60:100"\n}\n\n'
  + 'midi-clip checkedclip at 2 for auto {\n  notes melody.notes |> checked()\n}\n';
freshServer({ song });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

test('the song log switches assertions and the log level for this session, in the server and the browser', async ({ page }) => {
  await page.locator('#btabs button[data-t="log"]').click();
  await page.locator('[data-log-tab="song"]').click();
  const log = page.locator('#slog');
  await expect(log.locator('select[data-session="diagnostics.assertions"]')).toHaveValue('');
  await expect(log.locator('.song-diagnostic', { hasText: 'AMP6004' })).toHaveCount(0);   // off by default
  await expect(log.locator('.song-event')).toHaveCount(0);   // the default level keeps no log

  await log.locator('select[data-session="diagnostics.assertions"]').selectOption('on');
  await expect(log.locator('.song-diagnostic', { hasText: 'AMP6004' }).first()).toBeVisible();
  const browser = await page.evaluate(async (text) => (await import('/app/song-helpers.js')).assertionsForSong(text), song);
  expect(browser).toBe(true);   // the browser evaluation follows the same toggle

  await log.locator('select[data-session="logging.level"]').selectOption('info');
  await expect(log.locator('.song-event', { hasText: 'checking' }).first()).toBeVisible();

  const stored = await page.evaluate(async () => (await (await fetch('/api/diagnostics/session')).json()).values);
  expect(stored).toEqual({ 'diagnostics.assertions': 'on', 'logging.level': 'info' });
  expect(await page.evaluate(() => (window as any).AMPLE_STATE.doc.text)).not.toContain('logging.level');   // the song is not changed

  // back to "as the song": the problem and the events go away
  await log.locator('select[data-session="diagnostics.assertions"]').selectOption('');
  await log.locator('select[data-session="logging.level"]').selectOption('');
  await expect(log.locator('.song-diagnostic', { hasText: 'AMP6004' })).toHaveCount(0);
  await expect(log.locator('.song-event')).toHaveCount(0);
});

test('two quick choices end in the state of the last one, even when the first answer comes in late', async ({ page }) => {
  await page.locator('#btabs button[data-t="log"]').click();
  await page.locator('[data-log-tab="song"]').click();
  const log = page.locator('#slog');
  // the first read of the song after a choice is slow: without the queue, the second choice would finish first and the old answer would win
  let armed = false;
  await page.route(/\/api\/doc\?path=/, async (route) => {
    if (!armed || route.request().method() !== 'GET') { await route.continue(); return; }
    armed = false;
    const response = await route.fetch();   // the answer of that moment (log level info) ...
    await new Promise((resolve) => setTimeout(resolve, 1500));   // ... delivered after the second choice is done
    await route.fulfill({ response });
  });
  armed = true;
  await log.locator('select[data-session="logging.level"]').selectOption('info');
  await log.locator('select[data-session="logging.level"]').selectOption('');
  await expect.poll(async () => page.evaluate(async () => (await (await fetch('/api/diagnostics/session')).json()).values)).toEqual({});
  await page.waitForTimeout(2000);   // past the slow answer
  await expect(log.locator('.song-event')).toHaveCount(0);
  await expect(log.locator('select[data-session="logging.level"]')).toHaveValue('');
});

test('the log view filters levels, folds repeated events and opens the line of an event in Source', async ({ page }) => {
  await page.locator('#btabs button[data-t="log"]').click();
  await page.locator('[data-log-tab="song"]').click();
  const log = page.locator('#slog');
  await log.locator('select[data-session="logging.level"]').selectOption('info');
  await expect(log.locator('.song-event', { hasText: 'checking' })).toHaveCount(1);
  // the filter of a level hides it and shows it again; the count is on the button
  const info = log.locator('.song-log-filter[data-level="info"]');
  await expect(info).toContainText('info 1');
  await info.click();
  await expect(info).toHaveAttribute('aria-pressed', 'false');
  await expect(log.locator('.song-event', { hasText: 'checking' })).toHaveCount(0);
  await expect(log.locator('.song-log-events-empty')).toContainText('No event of the levels that are shown');
  await info.click();
  // an event of the song leads to its line
  await log.locator('.song-event', { hasText: 'checking' }).dblclick();
  await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  await log.locator('select[data-session="logging.level"]').selectOption('');
});
