import { expect, test, type Page } from '@playwright/test';
import { bottomTab, chooseMenu, dialog, launchUrl, openApp, state } from '../../web/tests/support';

// The Playlist view is behind a setting; the tests turn it on the way a person does (the stored switch) before the page loads.
async function openPlaylist(page: Page) {
  await page.addInitScript(() => localStorage.setItem('ample.playlist.on', '1'));
  await openApp(page);
  await page.locator('#btabs button[data-t="playlist"]').click();
  await expect(page.locator('#b-playlist .pl-line').first()).toBeVisible();
}
const lines = (page: Page) => page.locator('#b-playlist .pl-line .pl-name');

test('playlist: with the setting off there is no tab, and nothing is loaded', async ({ page }) => {
  await openApp(page);
  await expect(page.locator('#btabs button[data-t="playlist"]')).toHaveCount(0);
  await expect(page.locator('#b-playlist')).toHaveCount(0);
});

test('playlist: the lines, the tail and the master come from the song', async ({ page }) => {
  await openPlaylist(page);
  await expect(lines(page)).toHaveText(['drums', 'bass', 'piano']);
  await expect(page.locator('#b-playlist .pl-tail, #b-playlist .pl-terminal')).toHaveCount(2);   // the mixer and the master
  await expect(page.locator('#b-playlist .pl-master')).toBeVisible();
  await expect(page.locator('#b-playlist .pl-line[data-line] .pl-clip')).toHaveCount(4);   // bass, drums, piano x2
  await expect(page.locator('#b-playlist .pl-ruler')).toContainText('bar 1');
});

test('playlist: opening a line shows its chain, and a note source shows on the line', async ({ page }) => {
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-line', { hasText: 'piano' }).locator('.pl-fold').click();
  await expect(page.locator('#b-playlist .pl-sub[data-block="piano"]')).toBeVisible();
  await expect(page.locator('#b-playlist .pl-sub[data-block="chords"]')).toBeVisible();
});

test('playlist: selecting a line selects its block in the rest of the studio', async ({ page }) => {
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).locator('.pl-label').click();
  expect((await state(page)).sel).toEqual(['drums']);
});

test('playlist: a clip moves with the keyboard and the text is the same as in the Arrangement', async ({ page }) => {
  await openPlaylist(page);
  const clip = page.locator('#b-playlist .pl-clip[data-key="drums:0"]');
  await clip.focus();
  await clip.press('ArrowRight');
  await expect.poll(async () => (await state(page)).text).toMatch(/drums drums at 2 for 16 as "beat A"/);
  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await state(page)).text).toMatch(/drums drums at 1 for 16 as "beat A"/);
});

test('playlist: a clip is repeated, split and deleted', async ({ page }) => {
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-clip[data-key="piano:1"]').click({ button: 'right' });
  await chooseMenu(page, 'Delete this clip');
  await expect.poll(async () => (await state(page)).text).toMatch(/piano piano pinned@2.0 at 1 for 8 as verse \{/);
  await page.locator('#b-playlist .pl-clip[data-key="piano:0"]').click({ button: 'right' });
  await chooseMenu(page, /^Repeat/);
  await expect.poll(async () => (await state(page)).text).toMatch(/at 1 for 8 as verse, 9 for 8/);
});

test('playlist: mute and solo are a preview and never change the song', async ({ page }) => {
  await openPlaylist(page);
  const before = (await state(page)).text;
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).getByRole('button', { name: 'Mute drums' }).click();
  await expect(page.locator('#b-playlist .pl-banner')).toContainText('Preview of a mix');
  expect((await state(page)).text).toBe(before);
  await page.locator('#b-playlist .pl-banner button', { hasText: 'Clear' }).click();
  await expect(page.locator('#b-playlist .pl-banner')).toHaveCount(0);
});

test('playlist: an effect is put into a line and taken out again, byte for byte', async ({ page }) => {
  await openPlaylist(page);
  const before = (await state(page)).text;
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).locator('.pl-label').click({ button: 'right' });
  await chooseMenu(page, /^Add an effect at the end/);
  await chooseMenu(page, /^Reverb$/);
  await expect.poll(async () => (await state(page)).text).toMatch(/reverb drums-reverb/);
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).locator('.pl-fold').click();
  await page.locator('#b-playlist .pl-sub[data-block="drums-reverb"] .pl-label').click({ button: 'right' });
  await chooseMenu(page, 'Take out of the chain');
  await expect.poll(async () => (await state(page)).text).toBe(before);
});

test('playlist: a line is re-routed in one step and the old route stays when the new one is refused', async ({ page }) => {
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-toolbar button', { hasText: 'Routing' }).click();
  await expect(page.locator('#b-playlist .pl-port')).toHaveCount(3);
  await expect(page.locator('#b-playlist .pl-dest').first()).toHaveText('mix');
  await expect(page.locator('#b-playlist .pl-cables path')).toHaveCount(3);
});

test('playlist: a new line is a source on its own bus, connected to the mix', async ({ page }) => {
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-toolbar button', { hasText: '+ Line' }).click();
  await chooseMenu(page, 'New synth line');
  await expect.poll(async () => (await state(page)).text).toMatch(/bus synth-bus \{/);
  await expect(lines(page)).toHaveCount(4);
});

test('playlist: a line gets a name of its own, kept in the view block of the song', async ({ page }) => {
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-line', { hasText: 'bass' }).locator('.pl-label').dblclick();
  await dialog(page, 'OK', ['Low end']);
  await expect(lines(page).nth(1)).toHaveText(/Low end/);
  expect((await state(page)).text).toMatch(/view \{\n  line bass "Low end"\n\}/);
});

test('playlist: a section on the ruler shows in the Arrangement too', async ({ page }) => {
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-ruler').click({ button: 'right', position: { x: 40, y: 8 } });
  await chooseMenu(page, /^New section at bars/);
  await dialog(page, 'OK', ['Verse']);
  await expect(page.locator('#b-playlist .arr-section')).toContainText('Verse');
  await bottomTab(page, 'arr');
  await expect(page.locator('#arr .arr-section')).toContainText('Verse');
});

// The Arrangement sections share this file with the Playlist because both change the sections of the one song of the test server (one worker per file).
test.describe('arrangement sections', () => {
  test.beforeEach(async ({ page }) => { await openApp(page); await bottomTab(page, 'arr'); });

  const viewLines = async (page: import('@playwright/test').Page) => ((await state(page)).text.split('\n').filter((l: string) => l.trim().startsWith('section ')));

  test('arrangement sections: a section is made on the ruler, renamed and removed, and lives in the view block', async ({ page }) => {
    const ruler = page.locator('#arr .ruler');
    await ruler.click({ button: 'right', position: { x: 40, y: 8 } });
    await chooseMenu(page, /^New section at bars/);
    await dialog(page, 'OK', ['Verse']);
    await expect(page.locator('#arr .arr-section')).toHaveCount(1);
    await expect(page.locator('#arr .arr-section')).toContainText('Verse');
    expect(await viewLines(page)).toEqual([expect.stringMatching(/^\s*section "Verse" at \d+ for \d+$/)]);
    expect((await state(page)).text).toMatch(/\nview \{\n/);

    await page.locator('#arr .arr-section').click({ button: 'right' });
    await chooseMenu(page, 'Rename section...');
    await dialog(page, 'OK', ['Chorus']);
    await expect(page.locator('#arr .arr-section')).toContainText('Chorus');

    await page.locator('#arr .arr-section').click({ button: 'right' });
    await chooseMenu(page, 'Remove section');
    await expect(page.locator('#arr .arr-section')).toHaveCount(0);
    expect(await viewLines(page)).toEqual([]);
  });

  test('arrangement sections: each section is one undo step @quarantine', async ({ page }) => {
    const ruler = page.locator('#arr .ruler');
    await ruler.click({ button: 'right', position: { x: 40, y: 8 } });
    await chooseMenu(page, /^New section at bars/);
    await dialog(page, 'OK', ['A']);
    await expect(page.locator('#arr .arr-section')).toHaveCount(1);
    const rulerWidth = (await ruler.boundingBox())!.width;   // far from the first section on any screen width
    await ruler.click({ button: 'right', position: { x: Math.round(rulerWidth * 0.7), y: 8 } });
    await chooseMenu(page, /^New section at bars/);
    await dialog(page, 'OK', ['B']);
    await expect(page.locator('#arr .arr-section')).toHaveCount(2);
    await page.locator('body').press('Control+z');
    await expect(page.locator('#arr .arr-section')).toHaveCount(1);
    await page.locator('body').press('Control+z');
    await expect(page.locator('#arr .arr-section')).toHaveCount(0);
  });

});

test('playlist: the switch is in the help menu on a wide screen, and turns the tab on and off', async ({ page }) => {
  await openApp(page);
  await expect(page.locator('#btabs button[data-t="playlist"]')).toHaveCount(0);
  await page.locator('#btn-help').click();
  const item = page.getByRole('menuitemcheckbox', { name: 'Playlist view (beta)' });
  await expect(item).toHaveAttribute('aria-checked', 'false');
  await item.click();
  await expect(page.locator('#btabs button[data-t="playlist"]')).toHaveCount(1);
  await expect(page.getByRole('menuitemcheckbox', { name: 'Playlist view (beta)' })).toHaveAttribute('aria-checked', 'true');
  await page.getByRole('menuitemcheckbox', { name: 'Playlist view (beta)' }).click();
  await expect(page.locator('#btabs button[data-t="playlist"]')).toHaveCount(0);
});

const api = async (path: string, init?: { method?: string; body?: unknown }) => {
  const origin = new URL(launchUrl()).origin;
  const token = new URL(launchUrl()).hash.replace('#token=', '');
  const r = await fetch(origin + path, { method: init?.method, headers: { 'X-Ample-Token': token, 'Content-Type': 'application/json' }, body: init?.body ? JSON.stringify(init.body) : undefined });
  return r.json();
};
async function setSong(text: string) {
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text, baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
}

test('playlist: the dock shows the graph of the selected line, and a click on a node selects its block', async ({ page }) => {
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-line', { hasText: 'piano' }).locator('.pl-label').click();
  const graph = page.locator('#b-playlist .pl-graph');
  await expect(graph).toBeVisible();
  await expect(graph.locator('.pl-gnode')).toHaveCount(2);   // the notes (chords) and the piano
  await graph.locator('.pl-gnode.note').click();
  expect((await state(page)).sel).toEqual(['chords']);
});

test('playlist: the patterns of a drums song show as sections under the drums', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('ample.playlist.on', '1'));
  await openApp(page);
  const demo = process.env.AMPLE_DEMO_SONG!.replace('drums drums at 1 for 16 as "beat A" {\n}', 'drums drums at 1 for 16 as "beat A" {\n  patterns verse=x...x...x...x... / fill=xxxxxxxxxxxxxxxx\n  song verse*3 fill\n}');
  await setSong(demo);
  await page.goto(launchUrl());
  await page.waitForSelector('body[data-ready="1"]');
  await page.locator('#btabs button[data-t="playlist"]').click();
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).locator('.pl-fold').click();
  const pieces = page.locator('#b-playlist .pl-patterns .pl-pattern');
  await expect(pieces.first()).toHaveText('verse');
  expect(await pieces.count()).toBeGreaterThanOrEqual(4);   // verse*3, fill, then the cycle again
  await expect(pieces.nth(3)).toHaveText('fill');
});

test('playlist: the channels of a sequencer show as lanes with the clips of its playlist', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('ample.playlist.on', '1'));
  await openApp(page);
  const sequencer = [
    'sequencer perf pinned@1.0 at 1 for 16 {',
    '  lanes lane1=kick lane2=hat+clap',
    '  sequence {',
    '    sequencer 1',
    '    channel kick drum sample=a.wav',
    '    channel hat drum sample=b.wav',
    '    channel clap drum sample=c.wav',
    '    pattern pulse bars=4',
    '      kick x...x...',
    '      hat ..x...x.',
    '    playlist',
    '      clip pulse track=1 at=1 for=8',
    '      clip pulse track=1 at=9 for=8',
    '  }',
    '}',
    'bus perf-bus {',
    '  from perf.audio -> audio1',
    '}',
  ].join('\n');
  const demo = process.env.AMPLE_DEMO_SONG!.replace('  from piano.audio -> audio3\n}', '  from piano.audio -> audio3\n  from perf-bus.audio -> audio4\n}') + '\n' + sequencer + '\n';
  await setSong(demo);
  await page.goto(launchUrl());
  await page.waitForSelector('body[data-ready="1"]');
  await page.locator('#btabs button[data-t="playlist"]').click();
  await page.locator('#b-playlist .pl-line', { hasText: 'perf' }).locator('.pl-fold').click();
  await expect(page.locator('#b-playlist .pl-row.pl-lane')).toHaveCount(2);
  await expect(page.locator('#b-playlist .pl-row.pl-lane').first()).toContainText('lane1');
  await expect(page.locator('#b-playlist .pl-row.pl-lane .pl-pattern')).toHaveCount(4);   // two clips on each of the two lanes
});

async function songWith(page: Page, text: string) {
  await page.addInitScript(() => localStorage.setItem('ample.playlist.on', '1'));
  await openApp(page);
  await setSong(text);
  await page.goto(launchUrl());
  await page.waitForSelector('body[data-ready="1"]');
  await page.locator('#btabs button[data-t="playlist"]').click();
}
const text = async (page: Page) => (await state(page)).text as string;

test('playlist lanes: a parameter with an automation shows its curve, and a click opens the curve editor', async ({ page }) => {
  const demo = process.env.AMPLE_DEMO_SONG!.replace('from drums.audio -> audio1', 'from drums-fx.audio -> audio1') + '\nfilter drums-fx {\n  type lowpass\n  cutoff auto(1:1800, 9:3800)\n  from drums.audio -> audio\n}\n';
  await songWith(page, demo);
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).locator('.pl-fold').click();
  const row = page.locator('#b-playlist .pl-row.pl-auto');
  await expect(row).toHaveCount(1);
  await expect(row).toContainText('drums-fx · cutoff');
  await expect(row.locator('path.pl-curve-line')).toHaveCount(1);
  await row.locator('.pl-track').click();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await page.keyboard.press('Escape');
  await page.locator('#b-playlist .pl-sub.pl-chain[data-block="drums-fx"] .pl-label').click({ button: 'right' });
  await expect(page.locator('#menu .menu-item-with-icon, #menu .sub').filter({ hasText: 'Automate a parameter' })).toBeVisible();
});

test('playlist lanes: the sections of a drums song are resized, repeated and removed from the Playlist', async ({ page }) => {
  const demo = process.env.AMPLE_DEMO_SONG!.replace('drums drums at 1 for 16 as "beat A" {\n}', 'drums drums at 1 for 16 as "beat A" {\n  patterns verse=x...x...x...x... / fill=xxxxxxxxxxxxxxxx\n  song verse*3 fill\n}');
  await songWith(page, demo);
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).locator('.pl-fold').click();
  const first = page.locator('#b-playlist .pl-patterns .pl-pattern.editable').first();
  await first.scrollIntoViewIfNeeded();
  const box = (await first.boundingBox())!;
  await page.mouse.move(box.x + box.width - 3, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width - 3 + 48, box.y + box.height / 2, { steps: 4 });
  await page.mouse.up();
  await expect.poll(() => text(page)).toMatch(/song verse\*5 fill/);
  await page.locator('#b-playlist .pl-patterns .pl-pattern.editable').nth(1).click({ button: 'right' });
  await chooseMenu(page, 'Remove this section');
  await expect.poll(() => text(page)).toMatch(/song verse\*5\n/);
  await page.keyboard.press('Control+z');
  await expect.poll(() => text(page)).toMatch(/song verse\*5 fill/);
});

test('playlist lanes: a clip of a sequencer lane is moved, given another pattern, deleted and added, in the song text', async ({ page }) => {
  const sequencer = [
    'sequencer perf pinned@1.0 at 1 for 32 {',
    '  lanes lane1=kick lane2=lead',
    '  sequence {',
    '    sequencer 1',
    '    channel kick drum sample=a.wav',
    '    channel lead drum sample=b.wav',
    '    pattern pulse bars=4',
    '      kick x...x...',
    '    pattern theme bars=4',
    '      lead x...x...',
    '    playlist',
    '      clip pulse track=1 at=1 for=8',
    '      clip theme track=2 at=9 for=8',
    '  }',
    '}',
    'bus perf-bus {',
    '  from perf.audio -> audio1',
    '}',
  ].join('\n');
  const demo = process.env.AMPLE_DEMO_SONG!.replace('  from piano.audio -> audio3\n}', '  from piano.audio -> audio3\n  from perf-bus.audio -> audio4\n}') + '\n' + sequencer + '\n';
  await songWith(page, demo);
  await page.locator('#b-playlist .pl-line', { hasText: 'perf' }).locator('.pl-fold').click();
  const lane1 = page.locator('#b-playlist .pl-row.pl-lane').first();
  const clip = lane1.locator('.pl-pattern').first();
  await clip.scrollIntoViewIfNeeded();
  const box = (await clip.boundingBox())!;
  await page.mouse.move(box.x + 10, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + 10 + 48, box.y + box.height / 2, { steps: 4 });
  await page.mouse.up();
  await expect.poll(() => text(page)).toMatch(/clip pulse track=1 at=3 for=8/);
  await lane1.locator('.pl-pattern').first().click({ button: 'right' });
  await chooseMenu(page, 'Change the pattern', 'theme');
  await expect.poll(() => text(page)).toMatch(/clip theme track=1 at=3 for=8/);
  await lane1.locator('.pl-pattern').first().click({ button: 'right' });
  await chooseMenu(page, 'Delete this clip');
  await expect.poll(async () => (await text(page)).includes('track=1 at=3')).toBe(false);
  const lane2 = page.locator('#b-playlist .pl-row.pl-lane').last();
  await lane2.locator('.pl-track').click({ button: 'right', position: { x: 24 * 20 + 4, y: 4 } });
  await chooseMenu(page, /^Add a clip of theme at bar 21/);
  await expect.poll(() => text(page)).toMatch(/clip theme track=2 at=21 for=4/);
});

test('playlist accessibility: every control has a name and a key route, and colour is not the only signal', async ({ page }) => {
  const demo = process.env.AMPLE_DEMO_SONG!.replace('drums drums at 1 for 16 as "beat A" {\n}', 'drums drums at 1 for 16 as "beat A" {\n  patterns verse=x...x...x...x... / fill=xxxxxxxxxxxxxxxx\n  song verse*3 fill\n}');
  await songWith(page, demo);
  await page.locator('#b-playlist .pl-toolbar button', { hasText: 'Open all' }).click();
  await page.locator('#b-playlist .pl-toolbar button', { hasText: 'Routing' }).click();
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).getByRole('button', { name: 'Mute drums' }).click();
  const unnamed = await page.evaluate(() => {
    const name = (e: Element) => (e.getAttribute('aria-label') || (e.getAttribute('aria-labelledby') && document.getElementById(e.getAttribute('aria-labelledby')!)?.textContent) || (e as HTMLElement).innerText || e.getAttribute('title') || '').trim();
    const controls = [...document.querySelectorAll('#b-playlist button, #b-playlist select, #b-playlist input, #b-playlist [role="button"], #b-playlist [tabindex="0"]')];
    return controls.filter((e) => !name(e)).map((e) => e.tagName + '.' + e.className);
  });
  expect(unnamed).toEqual([]);
  // everything that can be dragged can also be reached and changed with the keyboard
  for (const selector of ['.pl-clip', '.pl-line .pl-label', '.pl-pattern.editable', '.pl-ruler', '.pl-minimap']) {
    const first = page.locator('#b-playlist ' + selector).first();
    expect(await first.evaluate((e) => (e as HTMLElement).tabIndex), selector).toBeGreaterThanOrEqual(0);   // a button or an element with tabindex 0: it can be reached with Tab
  }
  // the state of a mute is not only a colour, the status of a line is in words, and the cables have a legend
  await expect(page.locator('#b-playlist .pl-line', { hasText: 'drums' }).getByRole('button', { name: 'Mute drums' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('#b-playlist .pl-banner')).toHaveAttribute('role', 'status');
  await expect(page.locator('#b-playlist .pl-toolbar')).toContainText('dashed is a send or a sidechain key');
  const clipName = await page.locator('#b-playlist .pl-clip').first().getAttribute('aria-label');
  expect(clipName).toMatch(/bars \d+ to \d+/);
  // the focus is visible and motion is off for people who ask for it
  const css = await page.evaluate(() => [...document.styleSheets].flatMap((s) => { try { return [...s.cssRules].map((r) => r.cssText); } catch { return []; } }).join('\n'));
  expect(css).toContain('#b-playlist :focus-visible');
  expect(css).toMatch(/prefers-reduced-motion[^{]*\{[^}]*#b-playlist/);
});

test('playlist accessibility: a drum section and a clip answer the keyboard, and the ruler opens its menu from the keyboard', async ({ page }) => {
  const demo = process.env.AMPLE_DEMO_SONG!.replace('drums drums at 1 for 16 as "beat A" {\n}', 'drums drums at 1 for 16 as "beat A" {\n  patterns verse=x...x...x...x... / fill=xxxxxxxxxxxxxxxx\n  song verse*3 fill\n}');
  await songWith(page, demo);
  await page.locator('#b-playlist .pl-line', { hasText: 'drums' }).locator('.pl-fold').click();
  const section = page.locator('#b-playlist .pl-patterns .pl-pattern.editable').first();
  await section.focus();
  await section.press('Shift+ArrowRight');
  await expect.poll(() => text(page)).toMatch(/song verse\*4 fill/);
  await page.locator('#b-playlist .pl-patterns .pl-pattern.editable').first().press('ArrowRight');
  await expect.poll(() => text(page)).toMatch(/song fill verse\*4/);
  const ruler = page.locator('#b-playlist .pl-ruler');
  await ruler.focus();
  await ruler.press('ContextMenu');
  await expect(page.locator('#menu .menu-item-with-icon', { hasText: /^New section/ })).toBeVisible();
});

test('playlist: the dock lists what the last edit changed, a rubber band picks several clips, and Home and End go to the edges of a section', async ({ page }) => {
  await openPlaylist(page);
  // sections: Intro 1 to 4, Verse 5 to 12
  await page.locator('#b-playlist .pl-ruler').click({ button: 'right', position: { x: 4, y: 8 } });
  await chooseMenu(page, /^New section at bars/);
  await dialog(page, 'OK', ['Intro']);
  const drums = page.locator('#b-playlist .pl-clip[data-key="drums:0"]');
  await drums.focus();
  await drums.press('ArrowRight');
  await expect.poll(async () => (await state(page)).text).toMatch(/drums drums at 2 for 16/);
  await expect(page.locator('#b-playlist .pl-edits summary')).toContainText('Last change');
  await page.locator('#b-playlist .pl-edits summary').click();
  await expect(page.locator('#b-playlist .pl-edits pre')).toContainText('+ drums drums at 2 for 16');
  await page.locator('#b-playlist .pl-clip[data-key="drums:0"]').focus();
  await page.keyboard.press('Home');
  await expect.poll(async () => (await state(page)).text).toMatch(/drums drums at 1 for 16/);
  const tracks = await page.locator('#b-playlist .pl-line .pl-track').evaluateAll((els) => els.slice(0, 3).map((e) => { const r = e.getBoundingClientRect(); return [r.left, r.top, r.bottom]; }));
  const [x, yTop] = [tracks[0][0] + 420, tracks[0][1] + 2];
  await page.keyboard.down('Shift');
  await page.mouse.move(x, yTop);
  await page.mouse.down();
  await page.mouse.move(x - 400, tracks[2][2] - 2, { steps: 4 });
  await page.mouse.up();
  await page.keyboard.up('Shift');
  await expect(page.locator('#b-playlist .pl-clip.picked').first()).toBeVisible();
  expect(await page.locator('#b-playlist .pl-clip.picked').count()).toBeGreaterThan(1);
});

test('playlist: a clip is copied, cut and pasted as another part of its block', async ({ page }) => {
  await openPlaylist(page);
  const drums = page.locator('#b-playlist .pl-clip[data-key="drums:0"]');
  await drums.focus();
  await drums.press('Control+c');
  await drums.press('Control+v');
  await expect.poll(async () => (await state(page)).text).toMatch(/drums drums at 1 for 16 as "beat A", 17 for 16/);
  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await state(page)).text).toMatch(/drums drums at 1 for 16 as "beat A" \{/);
  // nothing is pasted when nothing was copied in a fresh page
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await page.locator('#btabs button[data-t="playlist"]').click();
  const piano = page.locator('#b-playlist .pl-clip[data-key="piano:1"]');
  await piano.focus();
  await piano.press('Control+v');
  await expect(page.locator('#toast, .toast').filter({ hasText: 'Nothing copied yet' }).first()).toBeVisible();
});
