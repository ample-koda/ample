import { test, expect } from '@playwright/test';
import { api, launchUrl, node, openApp, state, srcText, setSrc, openSourceFile } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); await page.click('#vs-source'); await expect.poll(() => srcText(page)).toMatch(/composition|master master|bar 9/); });

const text = (page) => srcText(page);
const type = async (page, t: string) => { await setSrc(page, t); };

test('song recovery opens the full diff before choosing saved or recovered work', async ({ page }) => {
  const project = await api('/api/project'), id = project.id ?? project.Id, saved = await text(page), recovered = saved.replace('bars 8', 'bars 12');
  expect(id).toBeTruthy(); expect(saved).toContain('bars 8');
  const recoveryPage = await page.context().newPage(), key = `ample.work:${id}:song.amplet`;
  await recoveryPage.addInitScript(({ storageKey, record }) => localStorage.setItem(storageKey, record), { storageKey: key, record: JSON.stringify({ v: 1, project: id, path: 'song.amplet', text: recovered, undo: [], redo: [], updated: Date.now() - 60_000 }) });
  await page.close(); await recoveryPage.goto(launchUrl());
  const viewer = recoveryPage.locator('.recovery-diff-dialog');
  await expect(viewer).toBeVisible();
  const box = await viewer.boundingBox(), viewport = recoveryPage.viewportSize();
  expect(box!.width).toBeGreaterThan(viewport!.width * 0.9); expect(box!.height).toBeGreaterThan(viewport!.height * 0.8);
  await expect(recoveryPage.locator('.recovery-diff-head')).toContainText('saved');
  await expect(recoveryPage.locator('.recovery-diff-head')).toContainText('recovered');
  await expect(recoveryPage.locator('.recovery-diff-summary')).toContainText('+1 lines');
  await expect(recoveryPage.locator('.recovery-diff-panes')).toContainText('bars 8');
  await expect(recoveryPage.locator('.recovery-diff-panes')).toContainText('bars 12');
  await expect(recoveryPage.locator('.recovery-diff-line.removed').first()).toContainText('bars 8');
  await expect(recoveryPage.locator('.recovery-diff-line.added').first()).toContainText('bars 12');
  await recoveryPage.locator('.recovery-diff-actions button', { hasText: 'Restore recovered work' }).click();
  expect((await api('/api/doc?path=song.amplet')).text).toContain('bars 12');
});

test('the editor shows the song and live diagnostics come from the language server', async ({ page }) => {
  expect(await text(page)).toContain('master master');
  await expect(page.locator('#se-status')).toContainText('0 error(s)');
  await type(page, (await text(page)).replace('from chords.notes -> notes', 'from chordz.notes -> notes'));
  await expect(page.locator('#se-prob .seerr.error').first()).toBeVisible();
  await expect(page.locator('#se-status')).toContainText('error(s)');
  await expect(page.locator('#se-cm .cm-ample-gutter')).toContainText('●');
  await expect(page.locator('.cm-content .sq.error').first()).toBeAttached();
});

test('apply is refused while there are errors and updates the working copy, not a revision, when the song compiles', async ({ page }) => {
  const good = await text(page);
  await type(page, good.replace('from chords.notes -> notes', 'from chordz.notes -> notes'));
  await expect(page.locator('#se-prob .seerr.error').first()).toBeVisible();
  await page.click('#se-apply');
  await expect(page.locator('#toast')).toContainText('Not applied');
  await type(page, good.replace('bars 8', 'bars 12'));
  await expect(page.locator('#se-status')).toContainText('0 error(s)');
  await page.click('#se-apply');
  await expect(page.locator('#toast')).toContainText('Applied to the working copy');
  expect((await state(page)).text).toContain('bars 12');
  await expect(page.locator('#b-dirty')).toBeVisible();
  await page.click('#vs-blocks');
  await expect(node(page, 'chords')).toBeVisible();
  await page.click('#btn-save');
  await expect(page.locator('#b-dirty')).toBeHidden();
  expect((await api('/api/doc?path=song.amplet')).dirty).toBe(false);
});

test('leaving the editor needs a song that compiles; revert brings the text back', async ({ page }) => {
  const good = await text(page);
  await type(page, good.replace('from chords.notes -> notes', 'from chordz.notes -> notes'));
  await expect(page.locator('#se-prob .seerr.error').first()).toBeVisible();
  await page.click('#vs-blocks');
  await expect(page.locator('#toast')).toContainText('fix them');
  await expect(page.locator('#se')).toBeVisible();
  await page.click('#se-revert');
  await page.locator('#dlg-btns button', { hasText: 'Revert' }).click();   // Revert asks first
  await expect.poll(() => text(page)).toBe(good);
  await page.click('#vs-blocks');
  await expect(page.locator('#se')).toBeHidden();
});

test('format and fix all use the code of the language library', async ({ page }) => {
  const good = await text(page);
  await type(page, good.replace('  chords Am F C G', '        chords Am F C G'));
  await page.waitForTimeout(800);
  await page.click('#se-format');
  await expect.poll(async () => (await text(page)).includes('        chords')).toBe(false);
  await expect(page.locator('#toast')).toContainText('Formatted');
});

test('rename changes the block and every reference', async ({ page }) => {
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => { const i = ta.value.indexOf('midi-clip chords') + 'midi-clip '.length + 2; ta.focus(); ta.setSelectionRange(i, i); });
  await page.click('#se-rename');
  await expect(page.locator('#dlg-extra input')).toBeVisible();
  await page.locator('#dlg-extra input').fill('harmony');
  await page.locator('#dlg-btns button', { hasText: 'Rename' }).click();
  await expect.poll(async () => text(page)).toContain('midi-clip harmony');
  expect(await text(page)).toContain('from harmony.notes -> notes');
  expect(await text(page)).not.toContain('midi-clip chords');
  expect(await text(page)).not.toContain('from chords.');
});

test('Ctrl+Space proposes block types and inserts one as a template', async ({ page }) => {
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => { ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length); ta.setRangeText('\n', ta.value.length, ta.value.length, 'end'); ta.dispatchEvent(new Event('input')); });
  await page.locator('#se-cm .cm-content').press('Control+Space');
  await expect(page.locator('#se-comp')).toBeVisible();
  await expect(page.locator('#se-comp')).toContainText('drums');
  await page.locator('#se-cm .cm-content').type('choi');
  await page.locator('#se-cm .cm-content').press('Control+Space');
  await page.locator('#se-comp div', { hasText: 'choir' }).first().dispatchEvent('mousedown');
  await expect.poll(async () => text(page)).toMatch(/choir choir \{/);
});

test('show block in the node editor needs the cursor inside a block', async ({ page }) => {
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => { const i = ta.value.indexOf('bass bass') + 12; ta.focus(); ta.setSelectionRange(i, i); });
  await page.click('#se-show');
  await expect(page.locator('#se')).toBeHidden();
  expect((await state(page)).sel).toEqual(['bass']);
});

test('the reference and the check output are one click away', async ({ page }) => {
  await page.click('#se-cli');
  await expect(page.locator('#dlg-t')).toHaveText('ample check');
  await page.locator('#dlg-btns button', { hasText: 'Close' }).click();
});

test('"Edit in source" on a block returns to the song even when another file is open', async ({ page }) => {
  await api('/api/file', { method: 'PUT', body: { path: 'notes.txt', text: 'a scratch file, not the song' } });
  await page.reload(); await page.waitForSelector('body[data-ready="1"]'); await page.click('#vs-source');
    await page.locator('#se-switch').fill('notes.txt');
  await page.locator('#se-switchlist [role="option"]', { hasText: 'notes.txt' }).first().click();
  await expect(page.locator('#se-title')).toHaveText('notes.txt');
  await expect.poll(() => text(page)).toContain('a scratch file');
  await page.click('#vs-blocks');
  await node(page, 'chords').getByRole('button', { name: 'Edit in source' }).click();
  await expect(page.locator('#se-title')).not.toHaveText('notes.txt');
  await expect.poll(() => text(page)).toContain('master master');
});

test('unsaved source files can switch into independent drafts and recover through a full-screen comparison', async ({ page }) => {
  await api('/api/file', { method: 'PUT', body: { path: 'notes.txt', text: 'saved notes' } });
  await api('/api/file', { method: 'PUT', body: { path: 'other.txt', text: 'other file' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await page.click('#vs-source');
 
  await openSourceFile(page, 'notes.txt');
  await expect(page.locator('#se-title')).toHaveText('notes.txt');
  await type(page, 'recovered notes\nsecond line');
  await openSourceFile(page, 'other.txt');
  await page.locator('#dlg-btns button', { hasText: 'Keep draft and open' }).click();
  await expect(page.locator('#se-title')).toHaveText('other.txt');
  // the draft of the file left behind is marked in the Recent menu
  await page.locator('#se-bar .recent-files-toggle').click();
  await expect(page.locator('#se-bar .recent-file-row[data-file="notes.txt"] .recent-file-dirty')).toHaveText('● Unsaved');
  await page.locator('#se-bar .recent-files-toggle').click();
  await page.evaluate(async () => {
    const { S } = await import('/app/store.js');
    const { workRecord } = await import('/lib/work-state.mjs');
    const record = workRecord({ project: S.project.id, path: 'other.txt', text: 'module buffer', undo: [], redo: [], now: Date.now() });
    localStorage.setItem(`ample.work:${S.project.id}:other.txt`, JSON.stringify(record));
  });

  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await page.click('#vs-source');
  await openSourceFile(page, 'notes.txt');
  await expect(page.locator('.recovery-diff-dialog')).toBeVisible();
  await expect(page.locator('.recovery-draft-list')).toHaveCount(0);   // only the file that is being opened is shown, not a list of every file
  await expect(page.locator('.recovery-diff-panes')).toContainText('saved notes');
  await expect(page.locator('.recovery-diff-panes')).toContainText('recovered notes');
  await page.locator('.recovery-diff-actions button', { hasText: 'Restore recovered work' }).click();
  await expect.poll(() => text(page)).toContain('recovered notes');
});

test('checkpoint checks every recovery draft before writing any of them', async ({ page }) => {
  await api('/api/file', { method: 'PUT', body: { path: 'notes.txt', text: 'saved notes' } });
  await api('/api/file', { method: 'PUT', body: { path: 'other.txt', text: 'saved other' } });
  const before = (await api('/api/history/graph')).nodes.length;
  await page.reload(); await page.waitForSelector('body[data-ready="1"]'); await page.click('#vs-source');
  await openSourceFile(page, 'notes.txt'); await expect(page.locator('#se-title')).toHaveText('notes.txt'); await type(page, 'draft notes');
  await expect.poll(() => srcText(page)).toBe('draft notes');
  await openSourceFile(page, 'other.txt'); await page.locator('#dlg-btns button', { hasText: 'Keep draft and open' }).click(); await expect(page.locator('#se-title')).toHaveText('other.txt');
  await type(page, 'draft other');
  await expect.poll(() => srcText(page)).toBe('draft other');
  await page.waitForTimeout(300);
  await expect.poll(() => page.evaluate(async () => { const { S } = await import('/app/store.js'); const { getRecoveryDraft } = await import('/lib/recovery-drafts.mjs'); return getRecoveryDraft(localStorage, S.project.id, 'other.txt')?.text; })).toBe('draft other');
  await api('/api/file', { method: 'PUT', body: { path: 'other.txt', text: 'externally changed' } });
  await page.click('#btn-cp'); await page.locator('#dlg-btns button', { hasText: 'Save all work and checkpoint' }).click();
  await expect(page.locator('#toast')).toContainText('Checkpoint stopped');
  await expect.poll(async () => (await api('/api/file?path=notes.txt')).text).toBe('saved notes');
  await expect.poll(async () => (await api('/api/file?path=other.txt')).text).toBe('externally changed');
  expect((await api('/api/history/graph')).nodes).toHaveLength(before);
});

test('checkpoint saves every pending source draft before creating one snapshot', async ({ page }) => {
  await api('/api/file', { method: 'PUT', body: { path: 'notes.txt', text: 'saved notes' } });
  await api('/api/file', { method: 'PUT', body: { path: 'other.txt', text: 'saved other' } });
  const beforeGraph = await api('/api/history/graph');
  const before = beforeGraph.nodes.length;
  await page.reload(); await page.waitForSelector('body[data-ready="1"]'); await page.click('#vs-source');
  await openSourceFile(page, 'notes.txt'); await expect(page.locator('#se-title')).toHaveText('notes.txt'); await type(page, 'draft notes');
  await openSourceFile(page, 'other.txt'); await page.locator('#dlg-btns button', { hasText: 'Keep draft and open' }).click();
  await page.click('#btn-cp'); await page.locator('#dlg-btns button', { hasText: 'Save all work and checkpoint' }).click();
  await expect.poll(async () => (await api('/api/file?path=notes.txt')).text).toBe('draft notes');
  await expect.poll(async () => (await api('/api/file?path=other.txt')).text).toBe('saved other');
  await expect.poll(async () => (await api('/api/history/graph')).nodes.length).toBe(before + 1);
  const afterGraph = await api('/api/history/graph');
  const previous = beforeGraph.lines.find((line: { name: string }) => line.name === 'garage').head;
  const current = afterGraph.lines.find((line: { name: string }) => line.name === 'garage').head;
  expect((await api(`/api/history/diff?from=${encodeURIComponent(previous)}&to=${encodeURIComponent(current)}`)).some((file: { path: string }) => file.path === 'notes.txt')).toBe(true);
});

test('checkpoint saves a dirty inactive song with a matching browser undo buffer', async ({ page }) => {
  const name = `recovery-${Date.now()}`;
  const created = await api('/api/doc/save-as', { method: 'POST', body: { path: 'song.amplet', name } });
  const path = created.path as string;
  const saved = await api(`/api/doc?path=${encodeURIComponent(path)}`);
  const changed = saved.text.replace(/tempo (\d+)/, (_match: string, value: string) => `tempo ${Number(value) + 3}`);
  await api('/api/doc', { method: 'PUT', body: { path, text: changed, baseVersion: saved.version } });
  await page.evaluate(({ project, path, text }) => localStorage.setItem(`ample.work:${project}:${path}`, JSON.stringify({ v: 1, project, path, text, undo: [], redo: [], updated: Date.now() })), { project: (await page.evaluate(() => (window as any).AMPLE_STATE.project.id)), path, text: changed });
  const before = (await api('/api/history/graph')).nodes.length;
  await page.click('#btn-cp'); await page.locator('#dlg-btns button', { hasText: 'Save all work and checkpoint' }).click();
  await expect.poll(async () => (await api(`/api/doc?path=${encodeURIComponent(path)}`)).dirty).toBe(false);
  expect((await api(`/api/doc?path=${encodeURIComponent(path)}`)).text).toBe(changed);
  await expect.poll(async () => (await api('/api/history/graph')).nodes.length).toBe(before + 1);
});

test('checkpoint batches song draft saves into one snapshot even when safe saves are enabled', async ({ page }) => {
  await api('/api/project/auto-checkpoint', { method: 'POST', body: { enabled: true, minutes: 5 } });
  const before = (await api('/api/history/graph')).nodes.length;
  await type(page, (await text(page)).replace('bars 8', 'bars 12'));
  await expect(page.locator('#b-dirty')).toBeVisible();
  await page.waitForTimeout(650);
  await page.click('#btn-cp'); await page.locator('#dlg-btns button', { hasText: 'Save all work and checkpoint' }).click();
  await expect.poll(async () => (await api('/api/history/graph')).nodes.length).toBe(before + 1);
});

test('checkpoint leaves conflicting drafts recoverable and does not create a partial snapshot', async ({ page }) => {
  await api('/api/file', { method: 'PUT', body: { path: 'notes.txt', text: 'saved notes' } });
  await api('/api/file', { method: 'PUT', body: { path: 'other.txt', text: 'saved other' } });
  await page.reload(); await page.waitForSelector('body[data-ready="1"]'); await page.click('#vs-source');
  await openSourceFile(page, 'notes.txt'); await expect(page.locator('#se-title')).toHaveText('notes.txt'); await type(page, 'draft notes'); await openSourceFile(page, 'other.txt');
  await page.locator('#dlg-btns button', { hasText: 'Keep draft and open' }).click();
  await api('/api/file', { method: 'PUT', body: { path: 'notes.txt', text: 'external edit' } });
  const before = (await api('/api/history')).log.length;
  await page.click('#btn-cp'); await page.locator('#dlg-btns button', { hasText: 'Save all work and checkpoint' }).click();
  await expect(page.locator('#toast')).toContainText('changed since its recovery draft');
  expect((await api('/api/history')).log).toHaveLength(before); await page.reload(); await page.waitForSelector('body[data-ready="1"]'); await page.click('#vs-source'); await openSourceFile(page, 'notes.txt');
  await expect(page.locator('.recovery-diff-dialog')).toBeVisible();
  await expect(page.locator('.recovery-diff-dialog')).toContainText('Recover unsaved work?');
});

test('closing a dirty source file can save it before returning to the song', async ({ page }) => {
  await api('/api/file', { method: 'PUT', body: { path: 'notes.txt', text: 'saved notes' } });
  await page.reload(); await page.waitForSelector('body[data-ready="1"]'); await page.click('#vs-source');
  await openSourceFile(page, 'notes.txt'); await expect(page.locator('#se-title')).toHaveText('notes.txt'); await type(page, 'saved on close'); await page.click('#se-close-file');
  await page.locator('#dlg-btns button', { hasText: 'Save and open' }).click();
  await expect(page.locator('#se-title')).toHaveText('song.amplet');
  expect((await api('/api/file?path=notes.txt')).text).toBe('saved on close');
});
