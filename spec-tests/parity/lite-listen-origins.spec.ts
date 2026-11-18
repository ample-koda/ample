import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// Live Preview in Remix: two chains read one melody. The notes of the melody that sound light up per chain (a colour and a number each), the "used by" row names
// the chains, hovering one shows only its notes, and a click goes to its block. The song is typed into the editor (the test seam ?hooks=1), a real build and a real sound.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/?hooks=1#token=');
const SONG = [
  'composition "origins" { ample 1  tempo 120  master-seed 1 }',
  'midi-clip theme at 1 for 1 {',
  '  melody "c4:1 d4:1 e4:1 g4:1"',
  '}',
  'midi-clip high at 1 for 1 {',
  '  notes theme.notes |> transpose(+12)',
  '}',
  'midi-clip low at 1 for 1 {',
  '  notes theme.notes |> transpose(-12)',
  '}',
  'synth lead at 1 for 1 {',
  '  wave saw',
  '  gain -14 dB',
  '  from high.notes -> notes',
  '}',
  'synth bass at 1 for 1 {',
  '  wave triangle',
  '  gain -12 dB',
  '  from low.notes -> notes',
  '}',
  '',
].join('\n');

async function openChainSong(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.click('#lite-demos-btn');
  await page.locator('#lite-demo-list li[data-demo="pixel-quest"] button').click();
  await expect(page.locator('#lite-editor .cm-content')).toContainText('PIXEL QUEST');
  await page.waitForFunction(() => (window as any).__remixTest.app.editor);
  await page.evaluate((text) => { const editor = (window as any).__remixTest.app.editor; editor.replaceRange(0, editor.getText().length, text); }, SONG);
  await expect(page.locator('#lite-editor .cm-content')).toContainText('midi-clip low');
  await expect(page.locator('#lite-saved')).toHaveText('saved on this browser', { timeout: 10000 });   // the build takes the text that is saved
}

/** The chains whose notes are lit in the editor right now (data-lp-name of the marks). */
const lit = (page: Page) => page.evaluate(() => [...new Set([...document.querySelectorAll('.cm-content .lp-now[data-lp-name]')].map((e) => e.getAttribute('data-lp-name')))].sort());

test('two chains of one melody: coloured notes per chain, a used-by row, hover isolates and a click jumps', async ({ page }) => {
  test.setTimeout(180000);
  await openChainSong(page);
  await page.click('#lite-listen-play');
  await expect(page.locator('#lite-listen-status')).toContainText('hearing build #1', { timeout: 90000 });
  const row = page.locator('#lite-listen-users');
  await expect(row).toBeVisible();
  await expect(row).toContainText('theme');
  await expect(row.locator('button.lp-user')).toHaveCount(2);
  const seen = new Set<string>();
  await expect.poll(async () => { for (const name of await lit(page)) seen.add(name as string); return [...seen].sort(); }, { timeout: 20000, intervals: [60] }).toEqual(['high', 'low']);
  const numbers = await page.evaluate(() => [...document.querySelectorAll('.cm-content .lp-now[data-lp-user]')].map((e) => e.getAttribute('data-lp-user')));
  expect(numbers.every((n) => n !== null && n !== ''), 'every lit note has a number as well as a colour').toBe(true);

  // hover: only the notes of that chain
  await row.locator('button.lp-user', { hasText: 'high' }).hover();
  await page.waitForTimeout(400);
  const during: string[][] = [];
  for (let i = 0; i < 12; i++) { during.push(await lit(page) as string[]); await page.waitForTimeout(80); }
  expect(during.flat().every((n) => n === 'high'), 'only high while hovering high').toBe(true);
  await expect.poll(async () => (await lit(page)).includes('high' as never), { timeout: 5000, intervals: [60] }).toBe(true);

  // a click goes to the block of the chain
  await row.locator('button.lp-user', { hasText: 'low' }).click();
  await page.keyboard.type('#');   // the caret is at the start of the block: what is typed shows where it is
  await expect.poll(() => page.evaluate(() => (window as any).__remixTest.app.editor.getText().includes('#midi-clip low'))).toBe(true);

  await page.click('#lite-listen-play');
  await expect(row).toBeHidden();
});
