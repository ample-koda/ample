import { test, expect } from '@playwright/test';
import { freshServer, openApp, launchUrl } from '../../web/tests/support';

freshServer();

test('many waveform cards fetch small summaries serially and never download the songs', async ({ page }) => {
  await openApp(page);
  let active = 0, maximum = 0, summaries = 0, audio = 0;
  await page.route('**/api/ai/tasks/budget/clip/*', async route => {
    if (!new URL(route.request().url()).searchParams.has('peaks')) { audio++; await route.abort(); return; }
    active++; maximum = Math.max(maximum, active);
    await new Promise(resolve => setTimeout(resolve, 15));
    summaries++; active--;
    await route.fulfill({ json: { points: 2, peaks: [-.5, .5, -.2, .2], seconds: 220 } });
  });
  await page.evaluate(async () => {
    const ai = await import('/app/ai.js');
    for (let i = 0; i < 40; i++) document.body.append(ai.waveformPlayer(`/api/ai/tasks/budget/clip/c${i}`, `Synthetic ${i}`));
  });
  await expect.poll(() => summaries).toBe(40);
  expect(maximum).toBe(1);
  expect(audio).toBe(0);
  await expect(page.locator('.ai-wave-time').first()).toHaveText('220.0 s');
});

test('a stalled indicator request never accumulates interval requests', async ({ page }) => {
  await openApp(page);
  let requests = 0;
  await page.route('**/api/ai/tasks/stalled?*', async route => {
    requests++;
    await new Promise(resolve => setTimeout(resolve, 6000));
    await route.fulfill({ json: { status: 'running', applied: false } }).catch(() => {});
  });
  await page.evaluate(async () => (await import('/app/ai.js')).trackTask('stalled'));
  await page.waitForTimeout(5300);
  expect(requests).toBe(1);
});

test('a read request can be aborted while waiting instead of blocking forever', async ({ page }) => {
  await openApp(page);
  await page.route('**/api/synthetic-stall', async route => { await new Promise(resolve => setTimeout(resolve, 1000)); await route.abort().catch(() => {}); });
  const aborted = await page.evaluate(async () => {
    const { api } = await import('/app/api.js');
    try { await api.get('/api/synthetic-stall', AbortSignal.timeout(100)); return false; }
    catch (error) { return error.name === 'TimeoutError' || error.name === 'AbortError'; }
  });
  expect(aborted).toBe(true);
});

test('hidden tabs release their event stream and reconnect on return', async ({ page }) => {
  await page.goto(new URL('/app/api.js', launchUrl()).toString());
  const result = await page.evaluate(async () => {
    const original = window.EventSource;
    let opened = 0, closed = 0, hidden = false;
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
    window.EventSource = class {
      constructor() { opened++; }
      addEventListener() {}
      close() { closed++; }
    } as any;
    try {
      const { connectEvents } = await import('/app/api.js');
      const stop = connectEvents({}, { hideGraceMs: 0 });
      hidden = true; document.dispatchEvent(new Event('visibilitychange'));
      const whileHidden = { opened, closed };
      hidden = false; document.dispatchEvent(new Event('visibilitychange'));
      stop();
      return { whileHidden, opened, closed };
    } finally { window.EventSource = original; delete (document as any).hidden; }
  });
  expect(result.whileHidden).toEqual({ opened: 1, closed: 1 });
  expect(result.opened).toBe(2);
  expect(result.closed).toBeGreaterThanOrEqual(2);
});

test('a tab that is hidden only briefly keeps its event stream (nothing reconnects or reloads on return)', async ({ page }) => {
  await page.goto(new URL('/app/api.js', launchUrl()).toString());
  const result = await page.evaluate(async () => {
    const original = window.EventSource;
    let opened = 0, closed = 0, hidden = false;
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
    window.EventSource = class {
      readyState = 1;
      constructor() { opened++; }
      addEventListener() {}
      close() { closed++; }
    } as any;
    try {
      const { connectEvents } = await import('/app/api.js');
      const stop = connectEvents({}, { hideGraceMs: 60_000 });
      hidden = true; document.dispatchEvent(new Event('visibilitychange'));
      hidden = false; document.dispatchEvent(new Event('visibilitychange'));
      const after = { opened, closed };
      stop();
      return after;
    } finally { window.EventSource = original; delete (document as any).hidden; }
  });
  expect(result).toEqual({ opened: 1, closed: 0 });
});
