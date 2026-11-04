import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The vocoder window shows the bands of the voice and of the carrier; a knob changes the number of bands and writes the setting into the block.
const SONG = `composition "demo" { ample 1  tempo 120  master-seed 1 }
midi-clip line at 1 for 2 {
  bars 2
  notes 1:0:4:57:100 2:0:4:60:100
}
synth voice at 1 for 2 {
  wave saw
  from line.notes -> notes
}
synth pad at 1 for 2 {
  wave saw
  from line.notes -> notes
}
vocoder talk {
  bands 12
  from voice.audio -> modulator
  from pad.audio -> carrier
}
master master {
  from talk.audio -> audio
}
`;
freshServer({ song: SONG });

const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('the window draws the bands and its knobs write the block', async ({ page }) => {
  await openApp(page, { reset: false });
  await node(page, 'talk').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const panel = page.locator('#studio[open] .studio-vocoder-panel');
  await expect(panel).toBeVisible();
  // twelve bands for the voice and twelve for the carrier
  await expect(panel.locator('.studio-vocoder-voice .studio-vocoder-band')).toHaveCount(12);
  await expect(panel.locator('.studio-vocoder-carrier .studio-vocoder-band')).toHaveCount(12);
  // the sliders of the knobs: all ten settings are there
  await expect(panel.locator('.studio-panel-control')).toHaveCount(10);
  const bands = panel.locator('.studio-panel-control', { hasText: 'Bands' }).getByRole('slider');
  await bands.focus();
  await page.keyboard.press('ArrowUp');
  await expect.poll(docText, { timeout: 8000 }).toMatch(/bands 13/);
  await expect(panel.locator('.studio-vocoder-voice .studio-vocoder-band')).toHaveCount(13);
});

test('Play works the sound out in the browser and a knob is heard while it plays', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'talk').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const play = page.locator('#studio[open] .vocoder-play');
  const status = page.locator('#studio[open] .vocoder-status');
  // the takes of the build reach the page a moment after it has loaded: try again while the window says it has nothing to play
  await expect.poll(async () => { if ((await play.getAttribute('aria-pressed')) === 'true') return true; if (/^(Build the blocks|Press Play)/.test(await status.innerText())) await play.click(); return false; }, { timeout: 30000, intervals: [500] }).toBe(true);
  await expect(page.locator('#studio[open] .vocoder-status')).toContainText('a knob is heard while it plays');
  // turning a knob while it plays keeps it playing (it is worked out again)
  const mix = page.locator('#studio[open] .studio-panel-control', { hasText: 'Mix' }).getByRole('slider');
  await mix.focus();
  await page.keyboard.press('ArrowDown');
  await expect(play).toHaveAttribute('aria-pressed', 'true', { timeout: 10000 });
  await play.click();
  await expect(play).toHaveAttribute('aria-pressed', 'false');
});
