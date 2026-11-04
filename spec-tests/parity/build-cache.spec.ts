import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

freshServer();

const statuses = (page) => page.evaluate(() => Object.fromEntries(Object.entries((window as any).AMPLE_STATE.build.status).map(([k, v]: [string, any]) => [k, v.status])));

async function build(page) {
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
}

test('an unchanged second build renders nothing and reports only cache hits; a changed parameter makes only its block and what follows stale', async ({ page }) => {
  await openApp(page);
  await build(page);
  const first = await statuses(page);
  expect(Object.values(first).every((s) => s === 'rendered')).toBe(true);
  expect(Object.keys(first).sort()).toEqual(['bass', 'chords', 'drums', 'master', 'mix', 'piano']);

  await build(page);
  const second = await statuses(page);
  expect(Object.values(second).every((s) => s === 'cached')).toBe(true);
  await expect(page.locator('#blog')).toContainText(/Build done: 6 cached, 0 rendered/);
  await expect(page.locator(".node.st-done[data-id=\"drums\"]")).toHaveCount(1);

  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'setParameter', block: 'drums', name: 'pattern', value: 'beat-B' } } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await build(page);
  const third = await statuses(page);
  expect(third.drums).toBe('rendered');
  expect(third.mix).toBe('rendered');
  expect(third.master).toBe('rendered');
  expect(third.chords).toBe('cached');
  expect(third.bass).toBe('cached');
  expect(third.piano).toBe('cached');
});

test('editing one block in the editor keeps the build of the blocks it does not feed', async ({ page }) => {
  await openApp(page);
  await build(page);
  const before = await page.evaluate(() => Object.keys((window as any).AMPLE_STATE.build.takes).sort());
  expect(before).toEqual(['bass', 'chords', 'drums', 'master', 'mix', 'piano']);
  const kept = await page.evaluate(async () => {
    const store = await import('/app/store.js');
    const doc = JSON.parse(JSON.stringify(store.S.doc));
    const drums = doc.blocks.find((b) => b.name === 'drums');
    drums.parameters.push({ name: 'pattern', value: 'beat-B' });   // a parameter the build has not seen
    store.setDoc(doc);
    return Object.keys(store.S.build.takes).sort();
  });
  expect(kept).toEqual(['bass', 'chords', 'piano']);   // drums and what reads it (mix, master) are stale
  const heading = await page.evaluate(async () => {
    const store = await import('/app/store.js');
    const doc = JSON.parse(JSON.stringify(store.S.doc));
    doc.project.tempo = (doc.project.tempo || 90) + 1;
    store.setDoc(doc);
    return Object.keys(store.S.build.takes).length;
  });
  expect(heading).toBe(0);   // the tempo changes every block
});

test('a new master seed only makes the blocks stale whose seed follows it', async ({ page }) => {
  await openApp(page);
  await build(page);
  const takes = () => page.evaluate(async () => Object.keys((await import('/app/store.js')).S.build.takes).sort());
  await page.evaluate(async () => { await (await import('/app/store.js')).refreshInputs(); });
  expect(await takes()).toEqual(['bass', 'chords', 'drums', 'master', 'mix', 'piano']);
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    const doc = JSON.parse(JSON.stringify(store.S.doc));
    doc.project.masterSeed = Number(doc.project.masterSeed) + 1;
    store.setDoc(doc);
  });
  const kept = await takes();
  expect(kept).toContain('chords');            // a chord clip has no seed
  expect(kept).not.toContain('drums');         // its seed is derived from the master seed
  expect(kept).not.toContain('master');        // and the mix follows what it reads
});

test('a song that was built before shows its blocks as built when it opens, without pressing Build', async ({ page }) => {
  await openApp(page);
  await build(page);
  await openApp(page, { reset: false });   // the page loads again; the takes are only in the take bank
  await expect.poll(() => page.evaluate(() => Object.keys((window as any).AMPLE_STATE.build.takes).sort())).toEqual(['bass', 'chords', 'drums', 'master', 'mix', 'piano']);
  expect(await statuses(page)).toEqual({ bass: 'cached', chords: 'cached', drums: 'cached', master: 'cached', mix: 'cached', piano: 'cached' });
});

test('a completed block stops showing live progress while other blocks build', async ({ page }) => {
  await openApp(page);
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    store.S.build = { running: true, status: {}, live: {}, log: [] };
    store.onBlockProgress({ block: 'drums', state: 'running', fraction: 0, steps: [] });
    store.onBlockProgress({ block: 'bass', state: 'running', fraction: 0, steps: [] });
  });
  await expect(page.locator('.node[data-id="drums"] .tag.stat')).toHaveText('building 0%');
  await page.evaluate(async () => (await import('/app/store.js')).onBuildProgress({ block: 'drums', status: 'rendered' }));
  await expect(page.locator('.node[data-id="drums"].st-done')).toHaveCount(1);
  await expect(page.locator('.node[data-id="drums"] .tag.stat')).toHaveCount(0);
  await expect(page.locator('.node[data-id="bass"] .tag.stat')).toHaveText('building 0%');
});

test('a renderer without an overall fraction shows indeterminate whole-block progress', async ({ page }) => {
  await openApp(page);
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    store.S.build = { running: true, status: {}, live: {}, log: [] };
    store.onBlockProgress({ block: 'drums', state: 'running', fraction: 0, steps: [{ name: 'rendering', fraction: 0.19 }] });
  });
  await expect(page.locator('.node[data-id="drums"] .nmore .pstep.head b')).toHaveText('…');
  await expect(page.locator('.node[data-id="drums"] .nbar')).toHaveClass(/busy/);
});

test('the header counts only blocks in the current build', async ({ page }) => {
  await openApp(page);
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    store.S.build = { ...store.S.build, running: true, only: 'drums', status: { drums: { status: 'cached' }, bass: { status: 'cached' } }, live: {}, queued: new Set(), completed: new Set() };
    store.onBlockProgress({ block: 'drums', state: 'queued', fraction: 0, steps: [] });
    store.onBlockProgress({ block: 'bass', state: 'queued', fraction: 0, steps: [] });
  });
  await expect(page.locator('#btxt')).toHaveText('building 0/2');
  await page.evaluate(async () => (await import('/app/store.js')).onBuildProgress({ block: 'drums', status: 'cached' }));
  await expect(page.locator('#btxt')).toHaveText('building 1/2');
});

test('cached blocks do not advance the estimate for renders', async ({ page }) => {
  await openApp(page);
  const progress = await page.evaluate(async () => {
    const store = await import('/app/store.js');
    store.S.build = { ...store.S.build, running: true, status: {}, live: {}, log: [], estimate: { blocks: 2, rendered: 0, startedAt: performance.now() - 1000, nextReport: 0.25 } };
    store.onBuildProgress({ block: 'chords', status: 'cached' });
    const afterCache = { rendered: store.S.build.estimate.rendered, reports: store.S.build.log.filter((x) => x.message.includes('expected renders')).length };
    store.onBuildProgress({ block: 'drums', status: 'rendered' });
    return { afterCache, rendered: store.S.build.estimate.rendered, reports: store.S.build.log.filter((x) => x.message.includes('expected renders')).length };
  });
  expect(progress).toEqual({ afterCache: { rendered: 0, reports: 0 }, rendered: 1, reports: 1 });
});
