import { test, expect } from '@playwright/test';
import { api, dialog, freshServer, openApp } from '../../web/tests/support';

// A checkpoint that is cancelled at the question about a conflict changes nothing: no file is written, no recovery copy is removed (#139).
freshServer({ files: { 'notes.txt': 'saved notes\n', 'other.txt': 'saved other\n' } });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

test('cancelling the checkpoint at a conflict keeps the saved files and both recovery copies as they were', async ({ page }) => {
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    const project = store.S.project?.id || '';
    // a recovery copy of a text file that could simply be saved ...
    localStorage.setItem(`ample.source-draft:${project}:notes.txt`, JSON.stringify({
      path: 'notes.txt', text: 'drafted notes\n', baseText: 'saved notes\n', updated: Date.now(),
    }));
    // ... and a working copy in this browser that differs from the file on the server (a conflict)
    localStorage.setItem(`ample.work:${project}:other.txt`, JSON.stringify({
      v: 1, project, path: 'other.txt', text: 'browser copy\n', undo: [], redo: [], updated: Date.now(),
    }));
  });

  const checkpointing = page.evaluate(() => import('/app/history.js').then((m) => m.checkpoint()));
  await dialog(page, 'Save all');                  // the first question: save all work and checkpoint
  await expect(page.locator('#dlg')).toContainText('other.txt');
  await dialog(page, 'Cancel');                    // the question about the conflict
  await checkpointing;

  expect((await api('/api/file?path=notes.txt')).text).toBe('saved notes\n');
  expect((await api('/api/file?path=other.txt')).text).toBe('saved other\n');
  const kept = await page.evaluate(async () => {
    const project = (await import('/app/store.js')).S.project?.id || '';
    return [
      localStorage.getItem(`ample.source-draft:${project}:notes.txt`) !== null,
      localStorage.getItem(`ample.work:${project}:other.txt`) !== null,
    ];
  });
  expect(kept).toEqual([true, true]);
});
