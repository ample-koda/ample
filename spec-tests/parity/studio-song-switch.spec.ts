import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { api, freshServer, openApp } from '../../web/tests/support';

// What belongs to one song must not follow the person into another song, and a whole new text must wait for the edits on their way
// (Studio bug hunt 2026-11-24: bugs 8, 9, 13 and 16).
const sequencerSong = fs.readFileSync(path.resolve('docs/clientside-preview/demo/advanced/sequencer.amplet'), 'utf8');
// the songs the tests switch to are files of the project from the start (a file written later is not a song the app opens)
freshServer({ files: { 'other.amplet': sequencerSong, 'a.amplet': sequencerSong, 'b.amplet': sequencerSong } });
const pathOf = (page: Page) => page.evaluate(() => (window as any).AMPLE_STATE?.path);

async function openSong(page: Page, name: string) {
  await page.evaluate((detail) => document.dispatchEvent(new CustomEvent('ample:open', { detail })), name);
  await expect.poll(() => pathOf(page)).toBe(name);
}

test('the bars chosen in one song are not the bars of the next song (bug 9)', async ({ page }) => {
  await openApp(page);
  await page.evaluate(async () => { const { S, notify } = await import('/app/store.js'); S.range = { firstBar: 40, bars: 8 }; notify(); });
  await openSong(page, 'other.amplet');
  expect(await page.evaluate(async () => (await import('/app/store.js')).S.range)).toBeNull();
});

test('the sequencer writes its changes into its own song and closes before another song opens (bug 16)', async ({ page }) => {
  await openApp(page);
  await openSong(page, 'a.amplet');
  await page.evaluate(async () => (await import('/app/sequencer.js')).openSequencer('sq'));
  await expect.poll(() => page.evaluate(async () => (await import('/app/views.js')).V.mode)).toBe('seq');
  // a change in the sequencer of song A: the verse gets one bar more
  await page.evaluate(async () => {
    const m = await import('/app/sequencer.js');
    const { S } = await import('/app/store.js');
    const block = S.doc.text;
    const changed = block.replace('pattern verse bars=2', 'pattern verse bars=3');
    const inner = changed.slice(changed.indexOf('sequencer 1'), changed.indexOf('  }\n}'));
    m.stageSequence(inner.split('\n').map((line: string) => line.replace(/^ {4}/, '')).join('\n'));
  });
  const before = await api('/api/doc?path=b.amplet');
  await openSong(page, 'b.amplet');
  // the workspace is closed, song B is as it was, and song A holds the change (in its working copy)
  expect(await page.evaluate(async () => (await import('/app/views.js')).V.mode)).toBe('blocks');
  expect((await api('/api/doc?path=b.amplet')).text).toBe(before.text);
  expect((await api('/api/doc?path=a.amplet')).text).toContain('pattern verse bars=3');
});

test('a whole new text waits for a knob edit on its way and does not take it back (bug 8)', async ({ page }) => {
  await openApp(page);
  // the answer of the first edit is slowed down, so the text is asked for while it is on its way
  let release: () => void = () => {};
  const held = new Promise<void>((resolve) => { release = resolve; });
  await page.route('**/api/doc/edits', async (route) => { const response = await route.fetch(); await held; await route.fulfill({ response }); });
  const result = await page.evaluate(async () => {
    const store = await import('/app/store.js');
    const block = store.flat().nodes.find((n: any) => n.params && Object.keys(n.params).length);
    const name = Object.keys(block.params)[0];
    const knob = store.edit([{ type: 'setParameter', block: block.id, name, value: '7' }]);
    const textBefore = store.S.doc.text;
    const text = store.applyText(`${textBefore}\n// a comment typed in the editor\n`);
    return { block: block.id, name, pending: [knob, text].length };
  });
  expect(result.pending).toBe(2);
  release();
  await expect.poll(() => page.evaluate(async () => (await import('/app/store.js')).editsDone().then(() => true))).toBe(true);
  const after = await page.evaluate(async () => (await import('/app/store.js')).S.doc.text);
  // the knob edit stays: the text made before it is refused instead of silently taking it back, and the undo stack holds one step per change
  expect(after).not.toContain('// a comment typed in the editor');
  const undoCount = await page.evaluate(async () => (await import('/app/store.js')).S.undo.length);
  expect(undoCount).toBe(1);
});

// Bug 13 (library details after Save notes) has no browser test yet: it needs a project with a linked library folder and an asset;
// library-manager.spec.ts has that set-up and is the place for it.
