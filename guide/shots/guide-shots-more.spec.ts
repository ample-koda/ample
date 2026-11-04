// The screenshots of the later pages of the guide: patterns, files, fades, the sequencer and library bundles. See guide-shots.spec.ts for the first pages.
import { expect, test } from '@playwright/test';
import { annotate, boxOf, built, fit, open, shot, unmark } from './lib';

test.use({ viewport: { width: 1500, height: 900 }, deviceScaleFactor: 1 });
test.setTimeout(150000);

const FOOT = `mixer mix stable@1.1 {
  from beat.audio -> in1
}
master master live@1.3 {
  from mix.mix -> mix
}
`;

const SECTIONS = `composition "sections" { ample 1  tempo 100  master-seed 3 }
drums beat stable@1.2 at 1 for 12 {
  kit samples/kit
  pattern kick:x...x...x...x... snare:....x.......x... hat:x.x.x.x.x.x.x.x.
  patterns verse=kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x. / fill=kick:xxxxxxxxxxxxxxxx snare:x.x.x.x.xxxxxxxx hat:xxxxxxxxxxxxxxxx / chorus=kick:x...x...x...x... snare:....x.......x..x hat:xxxxxxxxxxxxxxxx
  song verse*4 fill chorus*4 fill verse*2
}
${FOOT}`;

const BAND = `composition "band" { ample 1  tempo 96  master-seed 42 }
midi-clip chords stable@1.0 at 1 for 8 {
  chords Am F C G
}
drums beat stable@1.2 at 1 for 8 {
  kit samples/kit
  pattern kick:x.......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x.
}
bass bass stable@1.0 at 1 for 8 {
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

const FADES = `composition "fades" { ample 1  tempo 100  master-seed 7 }
drums groove-a stable@1.2 at 1 for 8 {
  kit samples/kit
  pattern kick:x...x...x...x... snare:....x.......x... hat:x.x.x.x.x.x.x.x.
}
drums groove-b stable@1.2 at 1 for 8 {
  kit samples/kit
  pattern kick:x..x..x...x.x... snare:....x.......x..x hat:xxxxxxxxxxxxxxxx
}
crossfade change stable@1.0 {
  from groove-a.audio -> a
  from groove-b.audio -> b
  start 5
  length 1
  shape equal
}
volume ending stable@1.0 {
  from change.out -> in
  gain fadeout(7, 6s, exp)
}
mixer mix stable@1.1 {
  from ending.mixed -> in1
}
master master live@1.3 {
  from mix.mix -> mix
}
`;

const SEQ = `sequencer 1
channel kick drum sample=samples/kit/kick.wav vol=-2 color=#e8505b
channel snare drum sample=samples/kit/snare.wav color=#f2994a
channel hat drum sample=samples/kit/hat.wav pan=14 vol=-6 color=#f2c94c
channel bass bass vol=-5 color=#6fcf97
channel lead notes wave=triangle vol=-9 color=#56ccf2
channel pad loop file=samples/loops/pad.wav bars=2 vol=-12 color=#7c83fd
pattern verse bars=2 color=#4a90d9
  kick x...x...x...x...|x...x...x..xx...
  snare ....x.......x...
  hat x.x.x.x.x.x.x.x.
  bass 1:0:1:45:100 1:2:1:45:90 2:0:1:43:100 2:2:1:43:90
  lead 1:0:1:69:96 1:1:1:72:96 1:2:2:76:96 2:0:2:74:96
  pad on
pattern chorus bars=2 color=#e8505b
  kick x...x...x...x...|x...x...x...x...
  snare ....x.......x..x
  hat xxxxxxxxxxxxxxxx
  bass 1:0:2:41:100 2:0:2:48:100
  lead 1:0:1:77:100 1:1:1:76:96 1:2:2:72:96 2:0:2:69:96
  pad on
playlist
  clip verse track=1 at=1 for=4
  clip chorus track=1 at=5 for=4
  section Verse at=1 for=4 color=#4a90d9
  section Chorus at=5 for=4 color=#e8505b
`;

const SEQUENCER = `composition "sequencer" { ample 1  tempo 104  master-seed 5 }
sequencer sq stable@1.0 at 1 for 8 {
  file sequences/groove.seq
  lanes lane1=kick lane2=snare+hat lane3=bass+lead lane4=pad
}
volume drums-bus stable@1.0 {
  from sq.lane2 -> in
  gain -2 dB
}
mixer mix stable@1.1 {
  from sq.lane1 -> in1
  from drums-bus.mixed -> in2
  from sq.lane3 -> in3
  from sq.lane4 -> in4
}
master master live@1.3 {
  from mix.mix -> mix
}
`;

test('patterns and sections', async ({ page }) => {
  const server = await open(page, SECTIONS, {}, true);
  try {
    await fit(page);
    await page.locator('.node[data-id="beat"]').click({ position: { x: 20, y: 36 } });
    await page.locator('#props [role="tab"][data-key="steps"]').click();
    await page.waitForTimeout(400);
    await annotate(page, [{ selector: '#seq-open-view', n: 1 }]);
    await shot(page, 'patterns-panel', await boxOf(page, '#right'));
    await unmark(page);
    await page.locator('#seq-open-view').click();
    await page.waitForTimeout(800);
    await shot(page, 'patterns-view', await boxOf(page, '#pt'));
    await page.click('#pt-back');
    await fit(page);
    await built(page);
    await shot(page, 'patterns-built', await boxOf(page, '#bottom'));
  } finally { await server.stop(); }
});

test('files and the project panel', async ({ page }) => {
  const server = await open(page, BAND, {}, true);
  try {
    await page.setViewportSize({ width: 1500, height: 1150 });   // room for the whole Add menu
    await page.waitForTimeout(400);
    await page.getByRole('button', { name: 'Project', exact: true }).click();
    await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
    await page.waitForTimeout(600);
    await shot(page, 'files-panel', await boxOf(page, '#bottom'));
    await page.locator('#project-panel-content').evaluate((host) => host.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 700, clientY: 760 })));
    await page.locator('#menu div', { hasText: 'Add' }).first().click();
    await page.waitForTimeout(300);
    await shot(page, 'files-add-menu', { x: 0, y: 560, width: 1500, height: 590 });
    await page.keyboard.press('Escape');
    await page.mouse.click(5, 5);
    await page.locator('.project-tile').filter({ hasText: 'kit' }).first().click({ button: 'right' });
    await page.locator('#menu div', { hasText: 'Rename…' }).click();
    await page.locator('#dlg-extra input').first().fill('rusty-kit');
    await page.locator('#dlg-btns button', { hasText: 'Next' }).click();
    await page.waitForTimeout(400);
    await shot(page, 'files-rename-preview', await boxOf(page, '#dlg', 16));
  } finally { await server.stop(); }
});

test('fades', async ({ page }) => {
  const server = await open(page, FADES, {}, true);
  try {
    await fit(page);
    await built(page);
    await shot(page, 'fades-graph', await boxOf(page, '#cv'));
    await page.locator('.node[data-id="ending"]').click({ position: { x: 20, y: 36 } });
    await page.locator('#props [role="tab"][data-key="parameters"]').click();
    await page.getByRole('button', { name: 'Automate gain' }).click();
    await page.waitForTimeout(500);
    await shot(page, 'fades-editor', await boxOf(page, '#dlg', 16));
  } finally { await server.stop(); }
});

test('the sequencer', async ({ page }) => {
  const server = await open(page, SEQUENCER, { 'sequences/groove.seq': SEQ }, true);
  try {
    await fit(page);
    await built(page);
    await shot(page, 'sequencer-block', await boxOf(page, '#cv'));
    await page.locator('.node[data-id="sq"]').click({ position: { x: 20, y: 36 } });
    await page.locator('#open-sequencer').click();
    await page.waitForTimeout(900);
    await annotate(page, [{ selector: '#sq-rack', n: 1 }, { selector: '#sq-patterns', n: 2 }, { selector: '#sq-playlist', n: 3 }]);
    await shot(page, 'sequencer-workspace', await boxOf(page, '#sq'));
    await unmark(page);
    await page.locator('#sq .sqmain').evaluate((m) => { m.scrollTop = m.scrollHeight; });
    await page.waitForTimeout(300);
    await shot(page, 'sequencer-lanes', await boxOf(page, '#sq-lanes', 8));
  } finally { await server.stop(); }
});

test('library bundles', async ({ page }) => {
  const server = await open(page, BAND, {}, true);
  try {
    await fit(page);
    await built(page);
    await page.locator('.node[data-id="beat"]').click({ button: 'right', position: { x: 20, y: 36 } });
    await page.waitForTimeout(300);
    await shot(page, 'bundles-menu', await boxOf(page, '#cv'));
    await page.locator('#menu div', { hasText: 'Save as bundle' }).first().click();
    await page.locator('dialog[open] input').first().fill('Beat A');
    await page.locator('dialog[open] button', { hasText: 'Save' }).first().click();
    await page.waitForTimeout(900);
    await page.mouse.click(800, 560, { button: 'right' });
    await page.locator('#menu div', { hasText: 'Bundle library' }).first().click();
    await page.waitForTimeout(500);
    await shot(page, 'bundles-library', await boxOf(page, '#dlg', 16));
    await page.locator('dialog[open] button', { hasText: 'Place in the song' }).click();
    await page.waitForTimeout(400);
    await page.locator('dialog[open] input').first().fill('intro-');
    await shot(page, 'bundles-place', await boxOf(page, '#dlg', 16));
    await page.locator('dialog[open] button', { hasText: /^Place$/ }).click();
    await page.waitForTimeout(1500);
    await fit(page);
    await shot(page, 'bundles-placed', await boxOf(page, '#cv'));
  } finally { await server.stop(); }
});

const MELODY = `composition "melody" { ample 1  tempo 100  master-seed 11 }
midi-clip lead stable@1.0 at 1 for 4 {
  notes "1:0:1:57:96 1:1:1:60:96 1:2:2:64:96 2:0:1:67:96 2:1:1:64:96 2:2:2:60:96 3:0:1:55:96 3:1:1:59:96 3:2:2:62:96 4:0:4:57:90"
}
piano keys stable@1.0 at 1 for 4 {
  from lead.notes -> notes
}
mixer mix stable@1.1 {
  from keys.audio -> in1
}
master master live@1.3 {
  from mix.mix -> mix
}
`;

const EFFECTS = `composition "effects" { ample 1  tempo 100  master-seed 5 }
midi-clip chords stable@1.0 at 1 for 8 {
  chords Am F C G
}
bass bass stable@1.0 at 1 for 8 {
  from chords.notes -> notes
}
eq tone stable@1.0 {
  low 4 dB
  from bass.audio -> in
}
delay echo stable@1.0 {
  time 375 ms
  feedback 35%
  mix 28%
  from tone.out -> in
}
reverb room stable@1.0 {
  size 45%
  decay 50%
  mix 22%
  from echo.out -> in
}
mixer mix stable@1.1 {
  from room.out -> in1
}
master master live@1.3 {
  from mix.mix -> mix
}
`;

test('midi and the piano roll', async ({ page }) => {
  const server = await open(page, MELODY, {}, true);
  try {
    await fit(page);
    await built(page);
    await shot(page, 'midi-melody-graph', await boxOf(page, '#cv'));
    await page.locator('.node[data-id="lead"] .nh .t').click();
    await page.getByRole('button', { name: /AMPLE Jam/ }).click();
    const dialog = page.locator('dialog.mls-panel');
    await dialog.waitFor();
    await page.waitForTimeout(700);
    await shot(page, 'piano-roll', await boxOf(page, 'dialog.mls-panel'));
    const canvas = dialog.locator('canvas.mls-grid');
    const box = (await canvas.boundingBox())!;
    await page.mouse.move(box.x + box.width * 0.2, box.y + box.height * 0.3);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.2 + 70, box.y + box.height * 0.3, { steps: 4 });
    await page.mouse.up();
    await page.waitForTimeout(500);
    await shot(page, 'piano-roll-drawn', await boxOf(page, 'dialog.mls-panel'));
  } finally { await server.stop(); }
});

test('effects', async ({ page }) => {
  const server = await open(page, EFFECTS, {}, true);
  try {
    await fit(page);
    await built(page);
    await shot(page, 'effects-graph', await boxOf(page, '#cv'));
    await page.locator('.node[data-id="tone"]').getByRole('button', { name: 'Advanced', exact: true }).click();
    await page.locator('#studio').waitFor();
    await page.waitForTimeout(700);
    await shot(page, 'effects-advanced', await boxOf(page, '#studio'));
  } finally { await server.stop(); }
});

test('the arrangement', async ({ page }) => {
  const server = await open(page, BAND, {}, true);
  try {
    await fit(page);
    await built(page);
    await page.waitForTimeout(500);   // the Arrangement is the tab that is open
    const ruler = (await page.locator('#arr .ruler').boundingBox())!;
    await page.mouse.move(ruler.x + 100, ruler.y + 8);
    await page.mouse.down();
    await page.mouse.move(ruler.x + 230, ruler.y + 8, { steps: 4 });
    await page.mouse.up();
    await page.waitForTimeout(400);
    await annotate(page, [{ selector: '#arr-preview', n: 1 }, { selector: '#arr .ruler', n: 2 }]);
    await shot(page, 'arrangement-range', await boxOf(page, '#bottom'));
    await unmark(page);
  } finally { await server.stop(); }
});

test('autocomplete in the Source view', async ({ page }) => {
  const server = await open(page, BAND);
  try {
    await page.getByRole('button', { name: 'Source', exact: true }).first().click();
    await expect(page.locator('#se-ta')).toHaveValue(/project/);
    await page.locator('#se-ta').click();
    await page.keyboard.press('Control+End');
    await page.keyboard.type('\nmidi-c');
    await expect(page.locator('#se-ghost .gh')).toHaveText('lip');
    await shot(page, 'autocomplete-ghost', await boxOf(page, '#se'));
    await page.keyboard.press('Escape');
    for (let i = 0; i < 7; i++) await page.keyboard.press('Backspace');
    await page.keyboard.type('\n# view pin ');
    await page.keyboard.press('Control+Space');
    await expect(page.locator('#se-comp')).toBeVisible();
    await page.keyboard.press('ArrowDown');
    await shot(page, 'autocomplete-list', await boxOf(page, '#se'));
  } finally { await server.stop(); }
});

test('packages and the AI assistant', async ({ page }) => {
  const server = await open(page, BAND, {}, true);
  try {
    await page.locator('.savemore').first().click();
    await page.waitForTimeout(300);
    await shot(page, 'package-save-menu', await boxOf(page, '#menu', 40));
    await page.locator('#menu div', { hasText: 'Export package' }).first().click();
    await expect(page.locator('#tx-preview .tx-tiles')).toBeVisible({ timeout: 120000 });
    await page.waitForTimeout(400);
    await shot(page, 'package-export-full', await boxOf(page, '#tx-export', 8));
    await page.locator('input[name="tx-mode"][value="derived"]').check();
    await expect(page.locator('#tx-preview .tx-tiles')).toBeVisible({ timeout: 120000 });
    await page.waitForTimeout(600);
    await shot(page, 'package-export-derived', await boxOf(page, '#tx-export', 8));
    await page.evaluate(() => document.querySelectorAll('dialog[open]').forEach((d) => (d as HTMLDialogElement).close()));
    await page.evaluate(() => import('/app/ai.js').then((m) => m.openAiSettings()));
    await expect(page.locator('#ai-status')).toBeVisible();
    await page.waitForTimeout(500);
    await shot(page, 'ai-settings', await boxOf(page, 'dialog[open]', 8));
  } finally { await server.stop(); }
});
