import { test, expect } from '@playwright/test';
import { freshServer, node, openApp, state } from '../../web/tests/support';

// The melody and chords editor of a midi-clip (web/app/melody-editor.js): opened from the block's button, the line stays the source, one undo step per gesture.
const SONG = 'composition "space" { ample 1  tempo 96  master-seed 13 }\n\nmidi-clip tune at 1 for 4 {\n  melody "e5:0.5 r:1.5 g5:0.5 r:1.5 c5:4"\n  bars 4\n}\n\nmidi-clip pad at 1 for 4 {\n  chords Dm Dm Bb F\n}\n';
freshServer({ song: SONG });
test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(async (text) => { await (await import('/app/store.js')).applyText(text); }, SONG);   // every test starts from the same song
});

const melodyLine = async (page: import('@playwright/test').Page) => (await state(page)).text.split('\n').find((l) => /^\s*melody\s/.test(l)) ?? '';

test('the block button opens the melody editor and drawing a note writes it into the melody line', async ({ page }) => {
  await node(page, 'tune').click();
  await page.getByRole('button', { name: 'Edit melody' }).click();
  const panel = page.locator('dialog.me-panel');
  await expect(panel).toBeVisible();
  await expect(panel.locator('.me-roll .note')).toHaveCount(3);
  const roll = panel.locator('.me-roll .hit');
  await roll.click({ position: { x: 52 * 9 + 5, y: 15 * 20 + 5 } });   // beat 10, free (the last note covers beats 5 to 8)
  await expect(panel.locator('.me-roll .note')).toHaveCount(4);
  await expect.poll(() => melodyLine(page)).toMatch(/melody "[^"]*:0\.5[^"]*"/);
  expect(await melodyLine(page)).toMatch(/c5:4 r:1 [a-g]#?[0-9]:0.5"/);   // a rest of one beat, then the new note
});

test('a note can be removed with the erase tool and the change is one undo step', async ({ page }) => {
  await node(page, 'tune').click();
  await page.getByRole('button', { name: 'Edit melody' }).click();
  const panel = page.locator('dialog.me-panel');
  const before = await melodyLine(page);
  await panel.getByRole('button', { name: 'Erase' }).click();
  await panel.locator('.me-roll .note').first().dispatchEvent('pointerdown', { button: 0 });
  await expect(panel.locator('.me-roll .note')).toHaveCount(2);
  await expect.poll(() => melodyLine(page)).not.toBe(before);
  await panel.getByRole('button', { name: 'Undo' }).click();
  await expect.poll(() => melodyLine(page)).toBe(before);
});

test('the melody text is kept when the editor cannot understand a word', async ({ page }) => {
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.edit([{ type: 'setParameter', block: 'tune', name: 'melody', value: '"c4:1 wobble d4:1"' }]);
  });
  const before = await melodyLine(page);
  await page.evaluate(async () => { (await import('/app/melody-editor.js')).openMelodyEditor('tune'); });
  await expect(page.locator('dialog.me-panel .me-problem')).toContainText('wobble');
  expect(await melodyLine(page)).toBe(before);
});

test('chords: a bar ribbon, and changing the quality rewrites the chords line', async ({ page }) => {
  await page.evaluate(async () => { (await import('/app/melody-editor.js')).openMelodyEditor('pad'); });
  const panel = page.locator('dialog.me-panel');
  await expect(panel.locator('.me-cb:not(.add)')).toHaveCount(4);
  await panel.locator('.me-cb').nth(2).click();
  await panel.locator('.me-insp select').nth(1).selectOption('m');
  await expect.poll(async () => (await state(page)).text).toContain('chords "Dm Dm Bbm F"');
});
