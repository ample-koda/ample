import { test, expect } from '@playwright/test';
import { api, bottomTab, dialog, freshServer, node, openApp } from '../../web/tests/support';

freshServer();
test.describe.configure({ mode: 'serial' });
test.beforeEach(async ({ page }) => { await openApp(page); });

const graph = () => api('/api/history/graph');

async function saveAndCheckpoint(page, bars: string) {
  await node(page, 'chords').locator('.nh .t').click();
  const input = page.locator('#props .prop', { hasText: 'bars' }).locator('input[type="number"]');
  await input.fill(bars);
  await input.press('Tab');
  await expect(page.locator('#b-dirty')).toBeVisible();
  await page.click('#btn-save');
  await expect(page.locator('#b-dirty')).toBeHidden();
  await page.click('#btn-cp');
  await dialog(page, 'Save all work and checkpoint', ['after the bars change']);
}

test('the header shows the line you are on and where studio is', async ({ page }) => {
  await expect(page.locator('#b-line')).toHaveText(/^on: \S+/);
  await expect(page.locator('#b-studio')).toContainText('★ studio');
});

test('a saved change and a checkpoint add a revision to the graph', async ({ page }) => {
  const before = (await graph()).nodes.length;
  expect((await graph()).nodes.every((entry) => entry.reduxRef == null)).toBe(true);
  await saveAndCheckpoint(page, '12');
  await expect.poll(async () => (await graph()).nodes.length).toBeGreaterThan(before);
  await expect.poll(async () => (await graph()).nodes.some((entry) => entry.reduxRef?.startsWith('AR-'))).toBe(true);
  await bottomTab(page, 'hist');
  await expect(page.locator('#hist g[data-node]').first()).toBeVisible();
  await expect(page.locator('#hist')).toContainText(/AR-[0-9]+/);
});

test('checkpoint saves the current dirty song before taking its single snapshot', async ({ page }) => {
  const before = (await graph()).nodes.length;
  await node(page, 'chords').locator('.nh .t').click();
  const input = page.locator('#props .prop', { hasText: 'bars' }).locator('input[type="number"]');
  await input.fill('14'); await input.press('Tab');
  await expect(page.locator('#b-dirty')).toBeVisible();
  await page.click('#btn-cp');
  await dialog(page, 'Checkpoint', ['unsaved song work']);
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text).toContain('bars 14');
  await expect.poll(async () => (await graph()).nodes.length).toBe(before + 1);
});

test('forking makes a new line and you are on it', async ({ page }) => {
  const name = `try-${Date.now()}`;
  await bottomTab(page, 'hist');
  await page.click('#btn-fork');
  await dialog(page, 'Fork', [name]);
  await expect(page.locator('#b-line')).toContainText(name);
  expect((await graph()).current).toBe(name);
});

test('go brings another line back', async ({ page }) => {
  await bottomTab(page, 'hist');
  await page.locator('#leaves .leaf[data-line="garage"] button', { hasText: 'go' }).click();
  await expect(page.locator('#b-line')).toContainText('on: garage');
  expect((await graph()).current).toBe('garage');
});

test('a revision can be tagged and the tag is listed', async ({ page }) => {
  const tag = `v-${Date.now()}`;
  await bottomTab(page, 'hist');
  await page.locator('#hist g[data-node] circle').first().click({ button: 'right' });
  await page.locator('#menu div', { hasText: 'Tag...' }).click();
  await dialog(page, 'Tag', [tag, 'test']);
  await expect.poll(async () => (await graph()).tags.map((t) => t.name)).toContain(tag);
  await expect(page.locator('#hist')).toContainText(`tag ${tag}`);
});

test('looking only is read-only and changes nothing on the server', async ({ page }) => {
  await bottomTab(page, 'hist');
  const before = JSON.stringify(await graph());
  const docBefore = JSON.stringify((await api('/api/doc?path=song.amplet')).text);
  await page.locator('#hist g[data-node] circle').first().click();
  await page.locator('#hdetail button', { hasText: 'Look only' }).click();
  await expect(page.locator('#banner')).toHaveClass(/show/);
  await expect(page.locator('#b-line')).toContainText('peek');
  await expect(page.locator('#btn-build')).toBeDisabled();
  await expect(page.locator('#btn-save')).toBeDisabled();
  await node(page, 'drums').locator('.nh .t').click();
  await page.keyboard.press('Delete');
  await expect(node(page, 'drums')).toBeVisible();
  expect(JSON.stringify(await graph())).toBe(before);
  await page.locator('#banner').click();
  await expect(page.locator('#banner')).not.toHaveClass(/show/);
  expect(JSON.stringify(await graph())).toBe(before);
  expect(JSON.stringify((await api('/api/doc?path=song.amplet')).text)).toBe(docBefore);
});

test('leaving a history preview restores the exact unsaved working copy', async ({ page }) => {
  await saveAndCheckpoint(page, '12');
  await node(page, 'chords').locator('.nh .t').click();
  const bars = page.locator('#props .prop', { hasText: 'bars' }).locator('input[type="number"]');
  await bars.fill('16'); await bars.press('Tab'); await expect(page.locator('#b-dirty')).toBeVisible();
  await bottomTab(page, 'hist');
  await page.locator('#hist g[data-node] circle').first().click();
  await page.locator('#hdetail button', { hasText: 'Look only' }).click();
  await page.locator('#banner').click();
  await expect(page.locator('#b-dirty')).toBeVisible();
  await node(page, 'chords').locator('.nh .t').click();
  await expect(page.locator('#props .prop', { hasText: 'bars' }).locator('input[type="number"]')).toHaveValue('16');
});

test('edit history previews undo states read-only, restores as one undoable edit, and marks exact Redux matches', async ({ page }) => {
  const saved = (await api('/api/doc?path=song.amplet')).text;
  const changed = saved.replace(/tempo (\d+)/, (_match, value) => `tempo ${Number(value) + 1}`);
  expect(changed).not.toBe(saved);
  await page.evaluate(async (text) => (await import('/app/store.js')).applyText(text), changed);
  await page.click('#btn-save');
  await expect(page.locator('#b-dirty')).toBeHidden();
  await bottomTab(page, 'hist');
  await page.click('#hist-edit-tab');
  const previous = page.locator('.edit-history-step', { hasText: 'Undo step 1' });
  await expect(previous).toContainText(/AR-[0-9]+/);
  await expect(page.locator('.edit-history-step.current')).not.toContainText(/AR-[0-9]+/);
  await previous.click();
  await expect(page.locator('#banner')).toContainText(/read-only/i);
  expect((await api('/api/doc?path=song.amplet')).text).toBe(changed);
  await page.click('#edit-history-return');
  await expect(page.locator('#banner')).not.toHaveClass(/show/);
  expect((await api('/api/doc?path=song.amplet')).text).toBe(changed);
  await previous.click();
  await page.click('#edit-history-restore');
  await expect(page.locator('#b-dirty')).toBeVisible();
  expect((await api('/api/doc?path=song.amplet')).text).toBe(saved);
  await page.click('#btn-undo');
  expect((await api('/api/doc?path=song.amplet')).text).toBe(changed);
  const forward = page.locator('.edit-history-step', { hasText: 'Redo step 1' });
  await expect(forward).toBeVisible();
  await forward.click();
  await expect(page.locator('#banner')).toContainText(/read-only/i);
  await expect(page.locator('.edit-history-step.current')).toContainText('Current work');
});

test('edit history double-click confirms restore and Ctrl-double-click restores directly', async ({ page }) => {
  const saved = (await api('/api/doc?path=song.amplet')).text;
  const changed = saved.replace(/tempo (\d+)/, (_match, value) => `tempo ${Number(value) + 1}`);
  await page.evaluate(async (text) => (await import('/app/store.js')).applyText(text), changed);
  await bottomTab(page, 'hist');
  await page.click('#hist-edit-tab');
  const previous = page.locator('.edit-history-step', { hasText: 'Undo step 1' });
  await previous.dblclick();
  await expect(page.locator('#dlg')).toBeVisible();
  await page.locator('#dlg-btns button', { hasText: 'Keep preview' }).click();
  await expect(page.locator('#banner')).toContainText(/read-only/i);
  await page.click('#edit-history-return');
  await expect(page.locator('#banner')).not.toHaveClass(/show/);
  expect((await api('/api/doc?path=song.amplet')).text).toBe(changed);
});

test('edit history Ctrl-double-click restores directly as one undoable edit', async ({ page }) => {
  const saved = (await api('/api/doc?path=song.amplet')).text;
  const changed = saved.replace(/tempo (\d+)/, (_match, value) => `tempo ${Number(value) + 1}`);
  await page.evaluate(async (text) => (await import('/app/store.js')).applyText(text), changed);
  await bottomTab(page, 'hist'); await page.click('#hist-edit-tab');
  const previous = page.locator('.edit-history-step', { hasText: 'Undo step 1' });
  await previous.dblclick({ modifiers: ['Control'] });
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text).toBe(saved);
  await expect(page.locator('#banner')).not.toHaveClass(/show/);
  await page.click('#btn-undo');
  expect((await api('/api/doc?path=song.amplet')).text).toBe(changed);
});

test('promote shows what changes and then studio equals garage', async ({ page }) => {
  await saveAndCheckpoint(page, String(13 + (Date.now() % 7)));
  await expect.poll(async () => (await api('/api/history/diff?from=studio&to=garage')).length).toBeGreaterThan(0);
  await bottomTab(page, 'hist');
  await page.click('#btn-promote');
  await expect(page.locator('#dlg-b')).toContainText('song.amplet');
  await dialog(page, 'Make main');
  await expect.poll(async () => (await api('/api/history/diff?from=studio&to=garage')).length).toBe(0);
});

test('dropping a line needs the typed acceptance and a reason', async ({ page }) => {
  const name = `scratch-${Date.now()}`;
  await bottomTab(page, 'hist');
  await page.click('#btn-fork');
  await dialog(page, 'Fork', [name]);
  await expect(page.locator('#b-line')).toContainText(name);
  await bottomTab(page, 'hist');
  await page.locator('#leaves .leaf[data-line="garage"] button', { hasText: 'go' }).click();
  await expect(page.locator('#b-line')).toContainText('on: garage');
  await page.locator(`#leaves .leaf[data-line="${name}"] button`, { hasText: 'drop' }).click();
  await expect(page.locator('#dlg-extra input').first()).toHaveAttribute('placeholder', /^type: drop /);
  const acceptance = ((await page.locator('#dlg-extra input').first().getAttribute('placeholder')) || '').replace('type: ', '');
  expect(acceptance.length).toBeGreaterThan(0);
  await dialog(page, 'Do it', ['not the right words', 'because']);
  await expect(page.locator('#toast')).toContainText('Type exactly');
  expect((await graph()).lines.find((l) => l.name === name).archived).toBe(false);
  await dialog(page, 'Do it', [acceptance, 'because']);
  await expect.poll(async () => (await graph()).lines.find((l) => l.name === name).archived).toBe(true);
});
