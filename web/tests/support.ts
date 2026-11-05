// Helpers shared by the parity specs.
import { expect, Page, test } from '@playwright/test';
import { ServerOptions, startServer } from './server';

let override: string | null = null;
let current: { folder: string; home: string } | null = null;
/** The shared server of this worker: one for each worker (global-setup.ts), so that workers never rewrite each other's song. */
const sharedUrl = () => {
  const urls: string[] = JSON.parse(process.env.AMPLE_LAUNCH_URLS || '[]');
  const index = Number(process.env.TEST_PARALLEL_INDEX ?? 0);
  return urls[index] ?? urls[0] ?? process.env.AMPLE_LAUNCH_URL!;
};
export const launchUrl = () => override ?? sharedUrl();
/** The folders of the server of this spec file (project and library home), when it has one of its own. */
export const ownServer = () => current;

/** Follow a nested block context menu and activate its final item. */
export async function chooseMenu(page: Page, ...labels: (string | RegExp)[]) {
  const exact = (label: string | RegExp) => label instanceof RegExp ? label : new RegExp(`^${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`);
  let scope = page.locator('#menu');
  for (const label of labels.slice(0, -1)) {
    const submenu = scope.locator(':scope > .sub').filter({ hasText: new RegExp(`^${String(label).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')} ›`) });
    await submenu.evaluate((el) => { const menu = el.closest('#menu')!; menu.scrollTop = el.offsetTop; });
    await submenu.evaluate((el) => el.dispatchEvent(new MouseEvent('mouseenter')));
    scope = submenu.locator(':scope > .flyout');
  }
  await scope.locator(':scope > .menu-item-with-icon').filter({ hasText: exact(labels.at(-1)!) }).evaluate((el) => (el as HTMLElement).click());
}

/**
 * Gives the specs of this file a server and project of their own (for specs that change history, seeds or takes,
 * so that what other files did earlier cannot matter).
 */
export function freshServer(options: ServerOptions = {}) {
  let stop: (() => Promise<void>) | null = null;
  test.beforeAll(async () => { const s = await startServer(options); override = s.launch; current = { folder: s.folder, home: s.home }; stop = s.stop; });
  test.afterAll(async () => { override = null; current = null; if (stop) await stop(); });
}

export function freshServerPerTest() {
  let stop: (() => Promise<void>) | null = null;
  test.beforeEach(async () => {
    const s = await startServer();
    override = s.launch;
    current = { folder: s.folder, home: s.home };
    stop = s.stop;
  });
  test.afterEach(async () => {
    override = null;
    current = null;
    if (stop) { await stop(); stop = null; }
  });
}
const token = () => /#token=(\w+)/.exec(launchUrl())![1];
const origin = () => new URL(launchUrl()).origin;

/** Puts the demo song back (through the API) and opens the app on a clean browser state. */
export async function openApp(page: Page, options: { reset?: boolean } = {}) {
  const headers = { 'X-Ample-Token': token(), 'Content-Type': 'application/json' };
  if (options.reset === false) {
    await page.goto(launchUrl());
    await page.waitForSelector('body[data-ready="1"]');
    return;
  }

  await fetch(`${origin()}/api/history/go`, { method: 'POST', headers, body: JSON.stringify({ line: 'garage' }) });
  const doc = await (await fetch(`${origin()}/api/doc?path=song.amplet`, { headers })).json();
  await fetch(`${origin()}/api/doc`, { method: 'PUT', headers, body: JSON.stringify({ path: 'song.amplet', text: process.env.AMPLE_DEMO_SONG, baseVersion: doc.version }) });
  await fetch(`${origin()}/api/doc/save`, { method: 'POST', headers, body: JSON.stringify({ path: 'song.amplet' }) });
  await page.goto(launchUrl());
  await page.waitForSelector('body[data-ready="1"]');
  await expect(page.locator('.node').first()).toBeVisible();
}

export const node = (page: Page, id: string) => page.locator(`.node[data-id="${id}"]`);

export const api = async (path: string, init?: { method?: string; body?: unknown }) => {
  const r = await fetch(`${origin()}${path}`, { method: init?.method ?? 'GET', headers: { 'X-Ample-Token': token(), 'Content-Type': 'application/json' }, body: init?.body === undefined ? undefined : JSON.stringify(init.body) });
  return r.json();
};

export async function addTemplateBlock(page: Page, type: string, name: string) {
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'addBlock', blockType: type, name } } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
}

/** Clicks a button of the dialog (waits for it to be open). */
export async function dialog(page: Page, label: string, fill?: string[]) {
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  if (fill) {
    const inputs = page.locator('#dlg-extra input');
    for (let i = 0; i < fill.length; i++) await inputs.nth(i).fill(fill[i]);
  }
  await page.locator('#dlg-btns button', { hasText: label }).click();
}

/** Shows a bottom panel tab. A click on the tab that is already open minimizes the panel, so only click when it is not showing. */
export async function bottomTab(page: Page, t: string) {
  const tab = page.locator(`#btabs button[data-t="${t}"]`);
  const showing = await tab.evaluate((el) => el.classList.contains('on') && !document.querySelector('#bottom')!.classList.contains('min'));
  if (!showing) await tab.click();
}

/** A point on the canvas where nothing is (no block, no control). */
export async function emptySpot(page: Page): Promise<{ x: number; y: number }> {
  const spot = await page.evaluate(() => {
    const r = document.getElementById('cv')!.getBoundingClientRect();
    for (let y = r.top + 40; y < r.bottom - 40; y += 30) {
      for (let x = r.left + 220; x < r.right - 40; x += 30) {
        const e = document.elementFromPoint(x, y);
        if (e && (e.id === 'cv' || e.id === 'world' || e.id === 'links' || e.tagName === 'svg')) return { x, y };
      }
    }
    return null;
  });
  if (!spot) throw new Error('no empty spot on the canvas');
  return spot;
}

export async function state(page: Page) {
  return page.evaluate(() => {
    const s = (window as any).AMPLE_STATE;
    return { text: s.doc.text as string, version: s.doc.version as number, view: { ...s.view }, sel: [...s.selSet] as string[], stack: [...s.stack] as string[] };
  });
}

/** Opens a tab of the properties panel (parameters, ports, notes, info, steps). */
export async function openTab(page: Page, key: string) {
  await page.locator(`#props [role="tab"][data-key="${key}"]`).click();
}

/** The source editor is CodeMirror: #se-ta is a hidden stand-in whose value/selection mirror it (read and set them with evaluate, never fill/inputValue/toBeVisible). */
/** Opens a project file in the Source tab through the file switcher (the list of files next to the text is gone). */
export async function openSourceFile(page: Page, name: string) {
  await page.locator('#se-switch').fill(name);
  await page.locator('#se-switchlist [role="option"]', { hasText: name }).first().click();
}
export const srcText = (page: Page) => page.locator('#se-ta').evaluate((e) => (e as HTMLTextAreaElement).value);
export const setSrc = (page: Page, text: string) => page.locator('#se-ta').evaluate((e, t) => { const ta = e as HTMLTextAreaElement & { oninput?: () => void }; ta.value = t; ta.oninput?.(); }, text);
