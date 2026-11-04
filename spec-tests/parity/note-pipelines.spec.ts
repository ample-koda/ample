import { test, expect, type Page } from '@playwright/test';
import { api, freshServerPerTest, node, openApp, srcText } from '../../web/tests/support';

// Note pipelines in the UI: the roll shows what a pipeline makes (read-only), its menu bakes it, turns it into plain notes or edits a hand-written piece; the editor marks a wrong
// operator and offers the fix. DOM only, no audio.
freshServerPerTest();

const SONG = `composition "demo" { ample 1  tempo 100  master-seed 1 }

midi-clip a at 1 for 1 {
  notes "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100"
}

midi-clip b at 2 for auto {
  notes a.notes
    |> transpose(+2)
    |> concat(
         editable {
           notes 1:0:1:70:100 1:1:1:72:100
         }
       )
}
`;

const SHORT = `composition "demo" { ample 1  tempo 100  master-seed 1 }

midi-clip a at 1 for 1 {
  notes "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100"
}

midi-clip b at 2 for auto {
  notes a.notes
    |> transpose(+2)
}
`;

async function setSong(page: Page, text: string) {
  await page.evaluate(async (t) => { const s = await import('/app/store.js'); await s.applyText(t); }, text);
}
const docText = async () => (await api('/api/doc?path=song.amplet')).text as string;

async function openJamOf(page: Page, block: string) {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, block).locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  return panel;
}
const noteCount = (panel: ReturnType<Page['locator']>) => panel.evaluate((d: any) => d.mlsDebug.model().notes.length as number);

test.beforeEach(async ({ page }) => {
  await openApp(page);
  await setSong(page, SONG);
});

test('a clip whose notes are a pipeline opens in the roll as the notes it makes, read-only, with the bake items in its menu', async ({ page }) => {
  const panel = await openJamOf(page, 'b');
  expect(await noteCount(panel)).toBe(5);   // three notes of a, a whole tone up, and the two hand-written ones
  await panel.getByRole('button', { name: 'Menu' }).click();
  await expect(page.getByRole('menuitem', { name: /Bake this pipeline/ })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: /Turn the pipeline into plain notes/ })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: /Edit the hand-written piece 1/ })).toBeVisible();
  expect(await docText()).toBe(SONG);   // opening the roll writes nothing
});

test('Bake this pipeline stores the notes signed in a stage of the song and keeps the pipeline', async ({ page }) => {
  const panel = await openJamOf(page, 'b');
  await panel.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitem', { name: /Bake this pipeline/ }).click();
  await expect(panel).toBeHidden();
  await expect.poll(docText).toContain('notes-hash');
  const text = await docText();
  expect(text).toContain('|> bake baked {');
  expect(text).toContain('input-hash');
  expect(text).toContain('1:0:1:62:100');
  expect(text).toContain('|> transpose(+2)');
});

test('Turn the pipeline into plain notes writes what it makes as an ordinary clip', async ({ page }) => {
  const panel = await openJamOf(page, 'b');
  await panel.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitem', { name: /Turn the pipeline into plain notes/ }).click();
  await expect.poll(async () => (await docText()).includes('|>')).toBe(false);
  expect(await docText()).toContain('1:0:1:62:100 1:1:1:64:100 1:2:1:66:100');
  // an ordinary clip again: the roll edits it
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'b').locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const again = page.locator('dialog.mls-panel');
  await expect(again).toBeVisible();
  await expect.poll(() => noteCount(again)).toBe(5);
});

test('the hand-written piece opens as its own editable clip and only that piece of the song changes', async ({ page }) => {
  const panel = await openJamOf(page, 'b');
  await panel.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitem', { name: /Edit the hand-written piece 1/ }).click();
  const piece = page.locator('dialog.mls-panel');
  await expect(piece).toBeVisible();
  await expect.poll(() => piece.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  expect(await noteCount(piece)).toBe(2);
  await piece.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+a');
  await page.keyboard.press('Delete');
  await expect.poll(async () => (await docText()).includes('1:0:1:70:100')).toBe(false);
  const text = await docText();
  expect(text).toContain('|> transpose(+2)');
  expect(text).toContain('editable {');
  expect(text.startsWith(SONG.slice(0, SONG.indexOf('editable {')))).toBe(true);
});

test('a wrong operator is underlined, the tooltip explains it and one click applies the fix', async ({ page }) => {
  await setSong(page, SONG.replace('transpose(+2)', 'transpos(+2)'));
  await page.click('#vs-source');
  await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  const box = (await page.locator('#se-cm .cm-scroller').boundingBox())!;
  const tip = page.locator('#se-errtip');
  await page.locator('#se-prob .seerr', { hasText: 'AMP2023' }).first().dblclick({ timeout: 15000 });   // the details of a problem open with a double click on it in the list (a plain hover shows nothing)
  await expect(tip).toBeVisible();
  await expect(tip).toContainText('Unknown operator');
  await expect(tip).toContainText('AMP2023');
  const fix = tip.getByRole('button', { name: /Replace with transpose/ });
  await expect(fix).toBeVisible({ timeout: 10000 });
  await fix.click();
  await expect.poll(() => srcText(page)).toContain('transpose(+2)');
});

test('a stale bake shows a warning with a button that bakes it again', async ({ page }) => {
  // a short song, so that the stage is on a line that is in view
  await setSong(page, SHORT);
  const baked = await page.evaluate(async () => {
    const response = await fetch('/api/notes/bake', { method: 'POST', headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token') || '', 'Content-Type': 'application/json' }, body: JSON.stringify({ path: 'song.amplet', block: 'b', stage: 'mid' }) });
    return (await response.json()).text as string;
  });
  await setSong(page, baked.replace('transpose(+2)', 'transpose(+5)'));
  await page.click('#vs-source');
  await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  await expect.poll(() => srcText(page)).toContain('|> bake mid');
  const lines = (await srcText(page)).split('\n');
  const stage = lines.findIndex((l) => l.includes('|> bake mid'));
  const box = (await page.locator('#se-cm .cm-scroller').boundingBox())!;
  const tip = page.locator('#se-errtip');
  await page.locator('#se-prob .seerr', { hasText: /stale/i }).first().dblclick({ timeout: 15000 });   // the details of a problem open with a double click on it in the list (a plain hover shows nothing)
  await expect(tip).toBeVisible();
  await expect(tip).toContainText('stale');
  await tip.getByRole('button', { name: 'Bake again' }).click();
  await expect.poll(async () => (await docText()).includes('transpose(+5)') && !(await docText()).includes('1:0:1:62:100')).toBe(true);
});
