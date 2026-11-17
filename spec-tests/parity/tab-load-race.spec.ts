import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Two big songs: opening the second while the first is still loading must never leave the text of one in the editor of the other (a Save would then write it over the wrong file).
// A big song keeps the page busy for seconds, so the test waits until everything has come to rest before it looks.
const NL = String.fromCharCode(10);
const big = (title: string) => {
  const lines = [`composition "${title}" { ample 1  tempo 120  master-seed 1 }`, ''];
  for (let i = 0; i < 500; i++) lines.push(`midi-clip c${i} at 1 for 1 {`, '  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100', '  bars 1', '}', '');
  lines.push('master out {', '}', '');
  return lines.join(NL);
};
freshServer({ files: { 'a.amplet': big('AAA'), 'b.amplet': big('BBB'), 'c.amplet': big('CCC') } });

const openSong = (page, path: string) => page.evaluate((p) => document.dispatchEvent(new CustomEvent('ample:open', { detail: p })), path);
const state = (page) => page.evaluate(async () => {
  const s = await import('/app/store.js');
  const ta = document.querySelector('#se-ta') as HTMLTextAreaElement | null;
  return `${s.S.path}|${/composition "(\w+)"/.exec(s.S.doc?.text || '')?.[1]}|${/composition "(\w+)"/.exec(ta?.value || '')?.[1]}`;
});
/** Waits until the state has not changed for a while. */
async function settle(page) {
  let last = '', same = 0;
  for (let i = 0; i < 160 && same < 12; i++) { await page.waitForTimeout(250); const now = await state(page); same = now === last ? same + 1 : 0; last = now; }
  return last;
}

for (const [slow, gap] of [[0, 0], [0, 100], [700, 150], [700, 500]]) {
  test(`the second song wins and its editor shows its own text (first load slowed ${slow} ms, second opened ${gap} ms later)`, async ({ page }) => {
    test.setTimeout(120000);
    await openApp(page, { reset: false });
    await page.click('#vs-source');
    await settle(page);
    if (slow) await page.route('**/api/doc?path=b.amplet*', async (route) => { await new Promise((r) => setTimeout(r, slow)); await route.continue(); });
    await openSong(page, 'b.amplet');
    await page.waitForTimeout(gap);
    await openSong(page, 'c.amplet');
    const final = await settle(page);
    console.log(`slow=${slow} gap=${gap} ->`, final);
    expect(final).toBe('c.amplet|CCC|CCC');
  });
}

// The window itself: the editor already belongs to the new song (SE.file) while its text area still shows the old one until the view is drawn. A Save in that window must not write the old text to the new song.
test('a save while the next song is still being drawn never writes the old text over it', async ({ page }) => {
  test.setTimeout(120000);
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await settle(page);
  // the old song is a.amplet: the tests above leave c.amplet as the last song, and typing in c itself would rightly be saved to c
  await openSong(page, 'a.amplet');
  expect(await settle(page)).toBe('a.amplet|AAA|AAA');
  const { setSrc, srcText, api } = await import('../../web/tests/support');
  await setSrc(page, `${await srcText(page)}${NL}# TYPED IN THE OLD SONG${NL}`);
  await page.waitForTimeout(600);
  await page.route('**/api/project/files*', async (route) => { await new Promise((r) => setTimeout(r, 3000)); await route.continue(); });   // the view of the new song is drawn after this answer
  await openSong(page, 'c.amplet');
  const keep = page.locator('#dlg button:has-text("Keep draft and open")');
  if (await keep.count() || await keep.waitFor({ timeout: 4000 }).then(() => true).catch(() => false)) await keep.click();
  await page.waitForTimeout(800);
  await page.keyboard.press('Control+s');
  await page.waitForTimeout(500);
  await page.keyboard.press('Control+Shift+s').catch(() => {});
  await settle(page);
  for (const file of ['b.amplet', 'c.amplet']) {
    const doc = await api(`/api/doc?path=${file}`);
    expect(String(doc.text), `${file} must not hold the typed text of the other song`).not.toContain('TYPED IN THE OLD SONG');
  }
  expect(String((await api('/api/doc?path=c.amplet')).text)).toContain('composition "CCC"');
});
