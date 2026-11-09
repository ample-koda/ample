import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// The components that ship with AMPLE (Voice clean, Quick trim) are in every project: in the Library under "Built in",
// usable by name without an import line.
freshServer({ song: 'composition "plain" { ample 1  tempo 100  master-seed 1 }\n' });

test('the built-in components are in the Library of a project that has no component file, and adding one writes no import line', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#ltabs button[data-l="clib"]').click();
  await expect(page.locator('#pane-clib')).toBeVisible();
  const entry = page.locator('#clib-list .clib-entry', { hasText: 'Voice clean' });
  await expect(entry).toHaveCount(1);
  await expect(page.locator('#clib-list summary', { hasText: 'Built in' }).first()).toBeVisible();
  await expect(entry.locator('button[aria-label^="Show "]')).toHaveCount(0);   // no file of the project to show
  const result = await page.evaluate(async () => {
    const { api: client } = await import('/app/api.js');
    const { S } = await import('/app/store.js');
    return client.post('/api/components/import', { path: S.path, file: 'builtin:voice-clean.amplec', name: 'ample.voice.clean', add: true });
  });
  expect(result.ok).toBe(true);
  expect(result.added.some((line: string) => line.startsWith('import'))).toBe(false);
  expect(result.added.join('\n')).toContain('ample.voice.clean clean {');
});

test('a song that names a built-in component sees it without an import line', async () => {
  const doc = await api('/api/doc?path=song.amplet');
  const text = `${doc.text}import-audio rec {\n  file samples/none.wav\n}\nample.voice.clean clean {\n  from rec.audio -> audio\n}\n`;
  const saved = await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text, baseVersion: doc.version } });
  const errors = (saved.diagnostics || []).filter((d: { severity: string }) => d.severity === 'error').map((d: { code: string; message: string }) => d.code + ' ' + d.message);
  expect(errors).toEqual([]);   // the block type is known without an import line
  expect(saved.text).toContain('ample.voice.clean clean {');
  expect(saved.text).not.toMatch(/^import /m);
});
