import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The task dialog has two tabs: Chat and Output (the samples Codex presents, with their number in the title). A new sample makes
// the tab pulse until it is opened; the cards of the Output pane are never taken out of the page, so what plays keeps playing and
// what the owner looks at stays where it is when a card is added.
freshServer();

const trialOfFirst = ['add_block: # ample-ignore L007', 'add_block: # ample-ignore L007', 'connect: from a.audio -> audio in b', 'set_param: b.mix: \'18%\' -> \'4%\''];
const clip = (n: number) => ({ id: `c${n}`, label: `Sample ${n}`, block: `blk${n}`, source: 'proposal', from: 0, to: 4, why: `Reason ${n}`, settings: [], trial: n === 1 ? trialOfFirst : [] });
const state = (clips: any[], logLines: number) => ({
  id: 'mock1', status: 'running', identity: 'Codex', modeLabel: 'Auto', prompt: 'x', thought: '', queueAhead: 0, canMessage: true, approvals: [], changes: [], checks: [], diagnostics: [], offered: [], moves: [],
  logSeq: logLines, log: Array.from({ length: logLines }, (_, i) => ({ seq: i + 1, kind: 'info', text: `line ${i}` })),
  build: { status: 'none', message: '', version: 0, failures: [], only: false, audio: false, skipRequested: false, progress: { done: 0, running: [] } }, clips,
});

test('Output tab: count, pulse, samples stay in place and the scroll position is kept', async ({ page }) => {
  let current = state([clip(1), clip(2), clip(3), clip(4), clip(5), clip(6)], 3);
  await openApp(page);
  await page.route(/\/api\/ai\/tasks\/mock1(\?.*)?$/, (route) => route.fulfill({ json: current }));
  await page.route('**/api/ai/tasks/mock1/clip/**', (route) => route.fulfill({ status: 404, body: '' }));
  await page.evaluate(() => import('/app/ai.js').then((m) => m.openAiTask('mock1')));
  const outputTab = page.locator('#ai-task [data-tab="output"]');
  await expect(outputTab).toHaveText('Output (6)');
  await expect(outputTab).toHaveClass(/pulse/);
  await expect(page.locator('#ai-output')).toBeHidden();

  await outputTab.click();
  await expect(page.locator('#ai-output')).toBeVisible();
  await expect(outputTab).not.toHaveClass(/pulse/);
  await expect(page.locator('#ai-output .ai-clip')).toHaveCount(6);
  await expect(page.locator('#ai-output .ai-clip').first()).toHaveAttribute('data-clip', 'c6');
  await expect(page.locator('#ai-output .ai-clip-mark-obsolete')).toHaveCount(0);
  await expect(page.locator('#ai-output .ai-clip-dismiss')).toHaveCount(6);
  await expect(page.locator('#ai-task .tx-body')).toHaveCSS('overflow-y', 'hidden');
  await expect(page.locator('#ai-output')).toHaveCSS('overflow-y', 'auto');
  // The composer gets the full row width, grows to three lines and then scrolls; Enter inserts a newline, modifier+Enter sends.
  const input = page.locator('#ai-msg-input');
  expect(await input.evaluate((e) => e.tagName)).toBe('TEXTAREA');
  const composeWidth = await page.locator('.ai-msg-compose').evaluate((e) => e.getBoundingClientRect().width);
  expect(await input.evaluate((e) => e.getBoundingClientRect().width)).toBeGreaterThan(composeWidth * 0.75);
  const heightBefore = await input.evaluate((e) => e.getBoundingClientRect().height);
  await input.click();
  await page.keyboard.type('first line');
  await page.keyboard.press('Enter');
  await page.keyboard.type('second line');
  await page.keyboard.press('Enter');
  await page.keyboard.type('third line');
  await page.keyboard.press('Enter');
  await page.keyboard.type('fourth line');
  expect(await input.evaluate((e) => (e as HTMLTextAreaElement).value)).toContain('first line\nsecond line');
  expect(await input.evaluate((e) => e.getBoundingClientRect().height)).toBeGreaterThan(heightBefore + 10);
  expect(await input.evaluate((e) => getComputedStyle(e).overflowY)).toBe('auto');
  const maxHeight = await input.evaluate((e) => parseFloat(getComputedStyle(e).lineHeight) * 3 + parseFloat(getComputedStyle(e).paddingTop) + parseFloat(getComputedStyle(e).paddingBottom) + parseFloat(getComputedStyle(e).borderTopWidth) + parseFloat(getComputedStyle(e).borderBottomWidth));
  expect(await input.evaluate((e) => e.getBoundingClientRect().height)).toBeLessThanOrEqual(maxHeight + 1);
  const messages: string[] = [];
  await page.route('**/api/ai/tasks/mock1/message', async (route) => { messages.push(route.request().postDataJSON().text); await route.fulfill({ json: { ok: true } }); });
  await page.keyboard.press('Control+Enter');
  await expect.poll(() => messages.length).toBe(1);
  expect(messages[0]).toBe('first line\nsecond line\nthird line\nfourth line');
  await expect(page.locator('#ai-msg-send')).toBeEnabled();
  await input.fill('send with alt');
  await page.keyboard.press('Alt+Enter');
  await expect.poll(() => messages.length).toBe(2);
  expect(messages[1]).toBe('send with alt');
  await expect(page.locator('#ai-task [data-tab="activity"]')).toHaveClass(/on/);
  await outputTab.click();
  const barStaysAtTop = await page.evaluate(() => {
    const body = document.querySelector('#ai-task .tx-body') as HTMLElement;
    const bar = document.querySelector('#ai-task .tx-body > .tx-seg') as HTMLElement;
    const filler = document.createElement('div');
    filler.style.cssText = 'height:1500px;flex:none';
    body.append(filler);
    const output = document.querySelector('#ai-output') as HTMLElement;
    output.scrollTop = 300;
    const result = { barTop: bar.getBoundingClientRect().top, bodyTop: body.getBoundingClientRect().top, scrollTop: output.scrollTop, scrollHeight: output.scrollHeight, clientHeight: output.clientHeight, position: getComputedStyle(bar).position };
    filler.remove();
    return result;
  });
  expect(Math.abs(barStaysAtTop.barTop - barStaysAtTop.bodyTop)).toBeLessThan(3);
  expect(barStaysAtTop.scrollTop).toBeGreaterThan(100);
  // a temporary trial: a short summary line, the edits one per line, the lint comments only counted
  const trial = page.locator('#ai-output [data-clip="c1"] details.ai-clip-settings');
  await expect(trial.locator('> summary')).toHaveText('Temporary trial · 2 change(s): 1 connection(s), 1 parameter change(s)');
  await expect(trial.locator('li')).toHaveCount(2);
  await expect(trial).toContainText('2 lint-ignore comment(s) not shown.');
  await page.evaluate(() => {
    const scroller = document.querySelector('#ai-output') as HTMLElement;
    (window as any).__card = document.querySelector('#ai-output [data-clip="c3"]');
    scroller.style.maxHeight = '260px';
    scroller.scrollTop = 150;
  });
  const top = () => page.evaluate(() => (document.querySelector('#ai-output [data-clip="c3"]') as HTMLElement).getBoundingClientRect().top);
  const before = await top();

  // a new sample arrives (at the front, the worst case) and new chat lines: nothing the owner looks at moves
  current = state([clip(1), clip(2), clip(3), clip(4), clip(5), clip(6), clip(0)], 9);
  await expect(outputTab).toHaveText('Output (7)');
  await expect(page.locator('#ai-output .ai-clip')).toHaveCount(7);
  await expect(page.locator('#ai-output .ai-clip').first()).toHaveAttribute('data-clip', 'c0');
  expect(await page.evaluate(() => (window as any).__card === document.querySelector('#ai-output [data-clip="c3"]'))).toBe(true);
  expect(Math.abs((await top()) - before)).toBeLessThan(2);
  await expect(page.locator('#ai-task [data-tab="activity"]')).toHaveClass(/pulse/);

  // away to the chat and back: the place in the Output is remembered, and the chat tab stops pulsing
  const scrolled = await page.evaluate(() => (document.querySelector('#ai-output') as HTMLElement).scrollTop);
  await page.locator('#ai-task [data-tab="activity"]').click();
  await expect(page.locator('#ai-output')).toBeHidden();
  await expect(page.locator('#ai-log')).toBeVisible();
  await outputTab.click();
  await expect.poll(() => page.evaluate(() => (document.querySelector('#ai-output') as HTMLElement).scrollTop)).toBeGreaterThan(scrolled - 3);
  expect(await page.evaluate(() => (window as any).__card === document.querySelector('#ai-output [data-clip="c3"]'))).toBe(true);
});

test('Codex summary appears in Output, Changes and Checks without moving the tab row', async ({ page }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', (error) => pageErrors.push(error.message));
  const current = { ...state([], 1), status: 'ready', summary: 'Codex explains the proposal here.' };
  await openApp(page);
  await page.route(/\/api\/ai\/tasks\/mock1(\?.*)?$/, (route) => route.fulfill({ json: current }));
  await page.evaluate(() => import('/app/ai.js').then((m) => m.openAiTask('mock1')));

  const tabs = page.locator('#ai-task .tx-body > .tx-seg');
  const outputTab = page.locator('#ai-task [data-tab="output"]');
  const chatTop = await tabs.evaluate((e) => e.getBoundingClientRect().top);
  await outputTab.click();
  await expect(outputTab).toHaveClass(/on/);
  expect(pageErrors).toEqual([]);
  await expect(page.locator('#ai-output')).toBeVisible();
  await expect(page.locator('#ai-output #ai-summary')).toContainText('Codex explains the proposal here.');
  expect(await page.locator('#ai-output #ai-summary').evaluate((e) => e.getBoundingClientRect().top)).toBeGreaterThan(await tabs.evaluate((e) => e.getBoundingClientRect().top));
  expect(Math.abs((await tabs.evaluate((e) => e.getBoundingClientRect().top)) - chatTop)).toBeLessThan(1);
  await page.locator('#ai-task [data-tab="changes"]').click();
  await expect(page.locator('#ai-task #ai-summary')).toContainText('Codex explains the proposal here.');
  expect(Math.abs((await tabs.evaluate((e) => e.getBoundingClientRect().top)) - chatTop)).toBeLessThan(1);
  await page.locator('#ai-task [data-tab="checks"]').click();
  await expect(page.locator('#ai-task #ai-summary')).toContainText('Codex explains the proposal here.');
  expect(Math.abs((await tabs.evaluate((e) => e.getBoundingClientRect().top)) - chatTop)).toBeLessThan(1);
});

test('sample choices send the referenced action on Ctrl+click and obsolete status is Codex-only', async ({ page }) => {
  const current = state([clip(2)], 1);
  const sent: { text: string }[] = [];
  await openApp(page);
  await page.route(/\/api\/ai\/tasks\/mock1(\?.*)?$/, (route) => route.fulfill({ json: current }));
  await page.route('**/api/ai/tasks/mock1/message', async (route) => { sent.push(route.request().postDataJSON()); await route.fulfill({ json: { ok: true } }); });
  await page.evaluate(() => import('/app/ai.js').then((m) => m.openAiTask('mock1')));
  await page.locator('#ai-task [data-tab="output"]').click();
  await expect(page.locator('#ai-output [data-clip-action]')).toHaveCount(3);
  await expect(page.locator('#ai-output .ai-clip-mark-obsolete')).toHaveCount(0);
  await page.locator('[data-clip-action="choose"]').click({ modifiers: ['Control'] });
  await expect.poll(() => sent.length).toBe(1);
  expect(sent[0].text).toContain('sample “Sample 2” (clip c2) for block “blk2”');
  expect(sent[0].text).toContain('I choose');
  await page.locator('#ai-task [data-tab="output"]').click();
  await page.locator('[data-clip-action="different"]').click({ modifiers: ['Control'] });
  await expect.poll(() => sent.length).toBe(2);
  expect(sent[1].text).toContain('clearly different sample for the same block');
  await page.locator('#ai-task [data-tab="output"]').click();
  await page.locator('[data-clip-action="more"]').click({ modifiers: ['Control'] });
  await expect.poll(() => sent.length).toBe(3);
  expect(sent[2].text).toContain('build and present more audio');
});
