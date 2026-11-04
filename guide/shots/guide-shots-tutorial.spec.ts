// The screenshots of the tutorial page ("Make a song"): the real song of docs/examples/24-tutorial-song with its real samples, so the pictures always match the example.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect, Page } from '@playwright/test';
import { startServer } from '../../web/tests/server';
import { annotate, boxOf, built, fit, shot, unmark } from './lib';

const here = path.dirname(fileURLToPath(import.meta.url));
const ample = path.resolve(here, '..', '..');
const example = path.join(ample, 'docs', 'examples', '24-tutorial-song');
test.use({ viewport: { width: 1500, height: 900 }, deviceScaleFactor: 1 });
test.setTimeout(180000);

/** Brings a block to the middle of the canvas, as the program does when you choose it from a list. */
async function focus(page: Page, id: string) {
  await page.evaluate(async (name) => {
    const store = await import('/app/store.js');
    const canvas = await import('/app/canvas.js');
    const node = store.current().nodes.find((n: { id: string }) => n.id === name);
    if (node) canvas.focusNode(node);
  }, id);
  await page.waitForTimeout(700);
}

const copyDir = (from: string, to: string) => fs.cpSync(from, to, { recursive: true });

test('the tutorial song', async ({ page }) => {
  const server = await startServer({
    song: fs.readFileSync(path.join(example, 'song.amplet'), 'utf8'),
    files: { 'sequences/band.seq': fs.readFileSync(path.join(example, 'sequences', 'band.seq'), 'utf8') },
    prepare: (_cli, folder) => {
      fs.mkdirSync(path.join(folder, 'samples'), { recursive: true });
      copyDir(path.join(ample, 'audio-packs/free/big-rusty-drums/kit'), path.join(folder, 'samples/kit'));
      copyDir(path.join(ample, 'audio-packs/free/emilyguitar/emily'), path.join(folder, 'samples/guitar'));
      copyDir(path.join(ample, 'audio-packs/free/black-and-blue-basses/babyblue'), path.join(folder, 'samples/bass'));
    },
  });
  try {
    await page.goto(server.launch);
    await page.waitForSelector('body[data-ready="1"]');
    await page.addStyleTag({ content: '#toast{display:none!important}' });
    await page.waitForTimeout(800);

    // the code, in three pictures: the band, the choir and its chain, the mix
    await page.getByRole('button', { name: 'Source', exact: true }).first().click();
    await page.waitForTimeout(900);
    const lines = (await page.locator('#se-ta').inputValue()).split('\n');
    const codeShot = async (needle: string, name: string, height: number) => {
      const at = lines.findIndex((l) => l.includes(needle));
      await page.locator('#se-ta').evaluate((el, top) => { (el as HTMLTextAreaElement).scrollTop = top; el.dispatchEvent(new Event('scroll')); }, Math.max(0, (at - 1) * 18 + 6));
      await page.waitForTimeout(300);
      const b = (await page.locator('#se-ta').boundingBox())!;
      await shot(page, name, { x: b.x - 60, y: b.y, width: Math.min(1000, b.width + 60), height: Math.min(height, b.height) });
    };
    await codeShot('# ---- the band ----', 'tut-code-band', 380);
    await codeShot('# ---- the choir', 'tut-code-choir', 560);
    await codeShot('# ---- the mix', 'tut-code-mix', 430);

    // the nodes (a fresh page: the source editor has done its work)
    await page.reload();
    await page.waitForSelector('body[data-ready="1"]');
    await page.addStyleTag({ content: '#toast{display:none!important}' });
    await page.waitForTimeout(900);
    await fit(page);
    await built(page);
    await fit(page);
    await shot(page, 'tut-graph', await boxOf(page, '#cv'));
    for (const [id, name] of [['band', 'tut-graph-band'], ['choir_ensemble', 'tut-graph-choir'], ['mix', 'tut-graph-mix']] as const) {
      await focus(page, id);
      await shot(page, name, await boxOf(page, '#cv'));
    }

    // the advanced controls of two effects (a taller window, so that the whole panel fits)
    await page.setViewportSize({ width: 1500, height: 1400 });
    await page.waitForTimeout(500);
    for (const [id, name] of [['choir_hall', 'tut-advanced-reverb'], ['choir_duck', 'tut-advanced-compressor']] as const) {
      await focus(page, id);
      await page.locator(`.node[data-id="${id}"]`).getByRole('button', { name: 'Advanced', exact: true }).click();
      await page.locator('#studio').waitFor();
      await page.waitForTimeout(800);
      await shot(page, name, await boxOf(page, '#studio'));
      await page.locator('#studio').getByRole('button', { name: 'Close advanced controls' }).click();
      await page.waitForTimeout(300);
    }

    await page.setViewportSize({ width: 1500, height: 900 });
    await page.waitForTimeout(500);

    // the sequencer
    await focus(page, 'band');
    await page.locator('.node[data-id="band"] .nh .t').click();
    await page.locator('#open-sequencer').click();
    await page.waitForTimeout(900);
    await page.locator('#sq-patterns .sqtab', { hasText: 'chorus' }).click();
    await page.waitForTimeout(500);
    await annotate(page, [{ selector: '#sq-rack', n: 1 }, { selector: '#sq-patterns', n: 2 }, { selector: '#sq-playlist', n: 3 }]);
    await shot(page, 'tut-sequencer', await boxOf(page, '#sq'));
    await unmark(page);
    await page.locator('#sq .sqmain').evaluate((m) => { m.scrollTop = m.scrollHeight; });
    await page.waitForTimeout(300);
    await shot(page, 'tut-lanes', await boxOf(page, '#sq-lanes', 8));
    await page.locator('#sq-undoback').click();
    await page.waitForTimeout(500);
    const confirm = page.locator('#dlg-btns button', { hasText: 'Undo and back' });
    if (await confirm.isVisible().catch(() => false)) await confirm.click();
    await page.waitForTimeout(500);

    // the choir notes in the piano roll
    await focus(page, 'choir_notes');
    await page.locator('.node[data-id="choir_notes"] .nh .t').click();
    await page.getByRole('button', { name: /AMPLE Jam/ }).click();
    await page.locator('dialog.mls-panel').waitFor();
    await page.waitForTimeout(800);
    await page.getByRole('button', { name: 'Fit the clip' }).click();
    await page.waitForTimeout(500);
    await shot(page, 'tut-choir-roll', await boxOf(page, 'dialog.mls-panel'));
    await page.getByRole('button', { name: 'Close AMPLE Jam' }).click();
    await page.waitForTimeout(300);

    // the whole song on the timeline
    await page.waitForTimeout(300);
    await shot(page, 'tut-arrangement', await boxOf(page, '#bottom'));
  } finally { await server.stop(); }
});
