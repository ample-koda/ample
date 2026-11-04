import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The advanced menu of a conversation: model and reasoning can be changed while it runs (Codex is told by the server), the memory fill and
// the usage of the account are visible without getting in the way (a chip), and Compact now works at any fill between two turns.
freshServer();

const state = (model: string | null, effort: string | null, extra: Record<string, unknown> = {}) => ({
  id: 'mock2', status: 'ready', identity: 'Codex', modeLabel: 'Auto', prompt: 'x', thought: '', queueAhead: 0, canMessage: true, approvals: [], changes: [], checks: [],
  logSeq: 1, log: [{ seq: 1, kind: 'info', text: 'hello' }], model, effort, compacting: false, compactions: 0, context: { used: 0, window: 0, percent: null },
  build: { status: 'none', message: '', version: 0, progress: { done: 0, running: [] } }, clips: [], ...extra,
});

const models = { default: 'gpt-6-luna', models: [{ id: 'gpt-6-luna', name: 'Luna' }, { id: 'gpt-6-sol', name: 'Sol' }], usage: { rateLimits: { limitName: 'Codex', primary: { usedPercent: 88, windowDurationMins: 300, resetsAt: 1900000000 }, secondary: { usedPercent: 40, windowDurationMins: 10080 } } } };

test('the advanced menu holds model, reasoning, memory and usage, and posts a change', async ({ page }) => {
  let current: any = state('gpt-6-luna', 'low', { context: { used: 100000, window: 200000, percent: 50 } });
  const posted: any[] = [];
  await openApp(page);
  await page.route('**/api/ai/models', (route) => route.fulfill({ json: models }));
  await page.route(/\/api\/ai\/tasks\/mock2(\?.*)?$/, (route) => route.fulfill({ json: current }));
  await page.route('**/api/ai/tasks/mock2/assistant', (route) => {
    const body = route.request().postDataJSON();
    posted.push(body);
    current = state(body.model || current.model, body.effort || current.effort, { context: current.context });
    return route.fulfill({ json: current });
  });
  await page.evaluate(() => import('/app/ai.js').then((m) => m.openAiTask('mock2')));

  // quiet by default: a chip with the fill and the usage, the menu closed
  await expect(page.locator('#ai-live-chip')).toContainText('memory 50%');
  await expect(page.locator('#ai-live-chip')).toContainText('usage 12% left');
  await expect(page.locator('#ai-advanced-panel')).toBeHidden();

  await page.locator('#ai-advanced').click();
  await expect(page.locator('#ai-advanced-panel')).toBeVisible();
  await expect(page.locator('#ai-live-model')).toHaveValue('gpt-6-luna');
  await expect(page.locator('#ai-live-effort')).toHaveValue('low');
  await expect(page.locator('#ai-stop-now')).toBeHidden();
  await expect(page.locator('#ai-ctx-text')).toContainText('50%');
  await expect(page.locator('#ai-usage-rows')).toContainText('12% left');
  await expect(page.locator('#ai-usage-advice')).toContainText('lighter model');

  await page.locator('#ai-live-effort').selectOption('high');
  await expect.poll(() => posted.length).toBeGreaterThan(0);
  expect(posted[0]).toMatchObject({ effort: 'high' });
  await page.locator('#ai-live-model').selectOption('gpt-6-sol');
  await expect.poll(() => posted.length).toBeGreaterThan(1);
  expect(posted[1]).toMatchObject({ model: 'gpt-6-sol', effort: 'high' });

  // Escape closes the menu
  await page.keyboard.press('Escape');
  await expect(page.locator('#ai-advanced-panel')).toBeHidden();
});

test('compact now works at any fill, and the chip warns while Codex compacts', async ({ page }) => {
  let current: any = state('gpt-6-luna', null, { context: { used: 100000, window: 200000, percent: 50 } });
  let compactPosts = 0;
  await openApp(page);
  await page.route('**/api/ai/models', (route) => route.fulfill({ json: models }));
  await page.route(/\/api\/ai\/tasks\/mock2(\?.*)?$/, (route) => route.fulfill({ json: current }));
  await page.route('**/api/ai/tasks/mock2/compact', (route) => { compactPosts += 1; current = { ...current, compacting: true }; return route.fulfill({ json: current }); });
  await page.evaluate(() => import('/app/ai.js').then((m) => m.openAiTask('mock2')));

  await page.locator('#ai-advanced').click();
  await expect(page.locator('#ai-compact')).toBeEnabled();   // at 50 percent, not only when nearly full
  await page.locator('#ai-compact').click();
  await expect.poll(() => compactPosts).toBe(1);
  await expect(page.locator('#ai-live-note')).toContainText('compacting its memory');
  await expect(page.locator('#ai-live-chip')).toContainText('compacting');
  await expect(page.locator('#ai-compact')).toBeDisabled();
  expect(await page.locator('#ai-live-settings').evaluate((e) => e.getBoundingClientRect().height)).toBeLessThan(40);
  expect(await page.locator('#ai-live-chip').evaluate((e) => Math.abs(e.getBoundingClientRect().top - document.querySelector('#ai-advanced')!.getBoundingClientRect().top))).toBeLessThan(3);
});

test('Esc closes only the advanced menu, focus goes in and back, and a failed model list offers Retry', async ({ page }) => {
  const current: any = state('gpt-6-luna', 'low');
  let fail = true;
  await openApp(page);
  await page.route('**/api/ai/models', (route) => (fail ? route.fulfill({ status: 500, json: { message: 'no' } }) : route.fulfill({ json: models })));
  await page.route(/\/api\/ai\/tasks\/mock2(\?.*)?$/, (route) => route.fulfill({ json: current }));
  await page.evaluate(() => import('/app/ai.js').then((m) => m.openAiTask('mock2')));
  await expect(page.locator('#ai-msg-send')).toHaveAttribute('title', 'Send (Ctrl+Enter)');
  await page.locator('#ai-advanced').click();
  await expect(page.locator('#ai-advanced-panel')).toBeFocused();
  await expect(page.locator('#ai-models-error')).toBeVisible();
  fail = false;
  await page.locator('#ai-models-retry').click();
  await expect(page.locator('#ai-live-model option')).toHaveCount(4);
  await expect(page.locator('#ai-models-error')).toBeHidden();
  await expect(page.locator('#ai-live-effort option', { hasText: 'Extra high' })).toHaveCount(1);
  await page.keyboard.press('Escape');
  await expect(page.locator('#ai-advanced-panel')).toBeHidden();
  await expect(page.locator('#ai-advanced')).toBeFocused();
  await expect(page.locator('#ai-live-chip')).toBeVisible();
});
