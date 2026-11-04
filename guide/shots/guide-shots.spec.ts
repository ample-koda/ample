// Makes the screenshots of the guide (guide/img/*.png) from a real AMPLE on a clean project with a small song. Run it with:  npm run guide:shots
// A screenshot is described here, once: which song, what to click, what to mark with numbers. When the program looks different, run it again and rebuild the guide.
import { test } from '@playwright/test';
import { annotate, boxOf, built, fit, open, shot, unmark } from './lib';

test.use({ viewport: { width: 1500, height: 900 }, deviceScaleFactor: 1 });
test.setTimeout(120000);

const SONG = `composition "my first song" { ample 1  tempo 100  master-seed 7 }

midi-clip chords stable@1.0 {
  chords Am F C G
  bars 8
}
drums beat stable@1.2 at 1 for 8 {
}
bass bass stable@1.1 at 1 for 8 {
  from chords.notes -> notes
}
mixer mix stable@1.1 {
  from beat.audio -> in1
  from bass.audio -> in2
}
master master live@1.3 {
  from mix.mix -> mix
}
`;

const BROKEN = `${SONG}
sequencer sq stable@1.0 at 1 for 4 {
}
`;

test('welcome, first song, bars', async ({ page }) => {
  const server = await open(page, SONG);
  try {
    await fit(page);
    await annotate(page, [{ selector: 'header', n: 1 }, { selector: '#left', n: 2 }, { selector: '#cv', n: 3 }, { selector: '#right', n: 4 }, { selector: '#bottom', n: 5 }]);
    await shot(page, 'welcome-window');
    await unmark(page);
    await shot(page, 'first-song-before', await boxOf(page, '#cv'));
    await built(page);
    await fit(page);
    await shot(page, 'first-song-after', await boxOf(page, '#cv'));
    await page.locator('#btabs button[data-t="arr"]').click();
    await page.waitForTimeout(600);
    await shot(page, 'bars-timeline', await boxOf(page, '#bottom'));
    await page.locator('#cv').click({ position: { x: 5, y: 5 } }).catch(() => {});
    await page.getByRole('button', { name: 'Source', exact: true }).first().click();
    await page.waitForTimeout(800);
    await shot(page, 'bars-source', await boxOf(page, '#se'));
  } finally { await server.stop(); }
});

test('the header and a failed build', async ({ page }) => {
  const server = await open(page, BROKEN);
  try {
    await fit(page);
    await page.click('#btn-build');
    await page.waitForSelector('#b-failed:visible');
    await page.waitForTimeout(600);
    await annotate(page, [{ selector: '#btn-build', n: 1 }, { selector: '#bprog', n: 2 }, { selector: '#btn-preview', n: 3 }, { selector: '#btn-stop', n: 4 }, { selector: '#btn-loop', n: 5 }, { selector: '#b-failed', n: 6 }]);
    await shot(page, 'transport-header', { x: 0, y: 0, width: 1500, height: 44 });
    await unmark(page);
    await page.click('#b-failed');
    await page.waitForTimeout(500);
    await annotate(page, [{ selector: '#blog div:nth-last-of-type(2)', n: 1 }, { selector: '#blog div:nth-last-of-type(2) .logblock', n: 2 }]);
    await shot(page, 'build-failed-log', await boxOf(page, '#bottom'));
    await unmark(page);
    await page.locator('#blog .logerr').last().click();
    await page.waitForTimeout(700);
    const ta = (await page.locator('#se-ta').boundingBox())!;
    const lines = (await page.locator('#se-ta').inputValue()).split('\n');
    const at = lines.findIndex((l) => l.startsWith('sequencer sq'));
    const scrolled = await page.locator('#se-ta').evaluate((el) => (el as HTMLTextAreaElement).scrollTop);
    const lineY = ta.y + 6 + 18 * (at + 0.5) - scrolled;
    await page.mouse.move(ta.x + 90, lineY);
    await page.waitForTimeout(500);
    const top = Math.max(ta.y, lineY - 90);
    await shot(page, 'build-failed-code', { x: ta.x - 60, y: top, width: 900, height: 300 });
  } finally { await server.stop(); }
});

test('versions', async ({ page }) => {
  const server = await open(page, SONG);
  try {
    await built(page);
    await page.locator('#btabs button[data-t="hist"]').click();
    await page.waitForTimeout(700);
    await annotate(page, [{ selector: '#btn-promote', n: 1 }, { selector: '#btn-fork', n: 2 }]);
    await shot(page, 'versions-history', await boxOf(page, '#bottom'));
    await unmark(page);
  } finally { await server.stop(); }
});
