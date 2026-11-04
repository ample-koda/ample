import { test, expect, type Page } from '@playwright/test';
import { freshServer, openApp, setSrc, srcText } from '../../web/tests/support';

// Pointing at a helper or a built-in in the source editor shows its signature, its doc comment (/** */ or ##) and what each parameter means. A block comment (/* */) is a comment.
freshServer();

const SONG = [
  'composition "p" { ample 1  tempo 100  master-seed 3 }',
  '',
  '/**',
  ' * Moves a voice up and delays it.',
  ' * @param source the notes to copy',
  ' * @param by how far up, in semitones',
  ' * @return the copy, shifted',
  ' * @example theme.notes |> canonVoice(+7st)',
  ' */',
  'helper canonVoice(Notes source, Interval by, Duration delay = 2bars) -> Notes {',
  '  source |> transpose(by) |> shift(delay)',
  '}',
  '',
  '/* the theme of the song,',
  '   two bars long */',
  '/** The theme. */',
  'midi-clip theme at 1 for 2 {',
  '  notes 1:0:1:60:100 1:1:1:62:100',
  '}',
  '',
  'midi-clip lead at 3 for auto {',
  '  notes theme.notes |> canonVoice(+7st) |> stretch(2) |> shift(1bar)',
  '}',
  '',
].join('\n');

/** The centre of a word in the editor (the first one at or after the occurrence), in client coordinates. */
async function pointOf(page: Page, needle: string, occurrence = 0) {
  return page.evaluate(({ needle, occurrence }) => {
    let seen = 0;
    for (const line of Array.from(document.querySelectorAll('.cm-line'))) {
      const text = line.textContent ?? '';
      for (let from = 0, at = text.indexOf(needle, from); at >= 0; from = at + 1, at = text.indexOf(needle, from)) {
        if (seen++ !== occurrence) continue;
        (line as HTMLElement).scrollIntoView({ block: 'center' });   // the editor shows only a part of the song
        const walker = document.createTreeWalker(line, NodeFilter.SHOW_TEXT);
        let start = 0;
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
          const length = node.textContent?.length ?? 0;
          if (at < start + length) {
            const range = document.createRange();
            range.setStart(node, at - start);
            range.setEnd(node, Math.min(length, at - start + needle.length));
            const r = range.getBoundingClientRect();
            return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
          }
          start += length;
        }
      }
    }
    return null;
  }, { needle, occurrence });
}

async function hover(page: Page, needle: string, occurrence = 0) {
  const p = await pointOf(page, needle, occurrence);
  expect(p, `"${needle}" is in the editor`).not.toBeNull();
  await page.mouse.move(p!.x - 40, p!.y + 40);
  await page.mouse.move(p!.x, p!.y, { steps: 4 });
  const tip = page.locator('#se-doctip');
  await expect(tip).toBeVisible({ timeout: 8000 });
  return tip;
}

async function openSong(page: Page, text: string, shows = 'canonVoice') {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  await setSrc(page, text);
  await expect(page.locator('.cm-content')).toContainText(shows);
}

test('a helper call shows its types, its doc comment, the notes of its parameters and what it returns', async ({ page }) => {
  await openSong(page, SONG);
  const tip = await hover(page, 'canonVoice', 1);   // the call in the lead clip
  await expect(tip).toContainText('canonVoice(Notes source, Interval by, Duration delay = 2bars) -> Notes');
  await expect(tip).toContainText('Moves a voice up and delays it.');
  await expect(tip).toContainText('Notes source — the notes to copy');
  await expect(tip).toContainText('Interval by — how far up, in semitones');
  await expect(tip).toContainText('Duration delay (default 2bars)');
  await expect(tip).toContainText('Returns the copy, shifted');
  await expect(tip.locator('.dt-badge')).toHaveText('helper (pure)');
  await expect(tip).toContainText('theme.notes |> canonVoice(+7st)');
});

test('a built-in operator shows its signature, what it does and what its parameters mean; a unit is explained too', async ({ page }) => {
  await openSong(page, SONG);
  let tip = await hover(page, 'shift(');
  await expect(tip).toContainText('shift(Duration by) -> Notes');
  await expect(tip).toContainText('How far to move the notes in time');
  await expect(tip.locator('.dt-badge')).toHaveText('Built in');
  await page.mouse.move(5, 5);
  await expect(page.locator('#se-doctip')).toBeHidden();
  tip = await hover(page, '1bar');   // the unit of 1bar
  await expect(tip).toContainText('unit');
  await expect(tip).toContainText('Length in bars');
});

test('a block comment is a comment and a doc comment above a block shows when its name is hovered', async ({ page }) => {
  await openSong(page, SONG);
  await expect(page.locator('.cm-content')).toContainText('two bars long');
  const tip = await hover(page, 'theme at');
  await expect(tip).toContainText('The theme.');
  await expect.poll(() => page.locator('#se-problems, .se-status, #se-status').first().textContent().catch(() => '')).not.toMatch(/AMP1116/);
});

test('a let, a var, a parameter and a loop variable show the type that was worked out, and the let keyword says what it declares', async ({ page }) => {
  const song = [
    'composition "p" { ample 1  tempo 100  master-seed 3 }',
    '',
    'helper phrase(Int count) -> Notes {',
    '  let motif = 1:0:0.5:76:86 1:1:0.25:79:78',
    '  var amount = 1',
    '  amount = 2.5',
    '  for step in 0..count {',
    '    emit motif |> shift(step * 1bar)',
    '  }',
    '  return motif',
    '}',
    '',
  ].join('\n');
  await openSong(page, song, 'phrase');
  let tip = await hover(page, 'motif', 0);
  await expect(tip).toContainText('let motif: Notes');
  await expect(tip).toContainText('from a list of notes');
  await expect(tip.locator('.dt-badge')).toHaveText('let');
  await page.mouse.move(5, 5);
  tip = await hover(page, 'amount', 0);
  await expect(tip).toContainText('var amount: Int | Float');
  await expect(tip).toContainText('Potential types');
  await page.mouse.move(5, 5);
  tip = await hover(page, 'step', 1);
  await expect(tip).toContainText('loop variable step: Int');
  await page.mouse.move(5, 5);
  tip = await hover(page, 'let motif');
  await expect(tip).toContainText('Declares motif: Notes');
});

test('a warning about a doc comment has a quick fix button that changes the comment', async ({ page }) => {
  const song = [
    'composition "p" { ample 1  tempo 100  master-seed 3 }',
    '',
    '/**',
    ' * @param source the notes',
    ' * @param byy how far',
    ' */',
    'helper up(Notes source, Interval by) -> Notes {',
    '  source |> transpose(by)',
    '}',
    '',
  ].join('\n');
  await openSong(page, song, 'helper up');
  const line = page.locator('.cm-line', { hasText: 'helper up(' }).first();
  await line.scrollIntoViewIfNeeded();
  const box = await line.boundingBox();
  await page.locator('#se-prob .seerr').first().dblclick({ timeout: 15000 });   // the details of a problem open with a double click on it in the list
  const fix = page.locator('#se-errtip .et-fix', { hasText: 'Rename @param byy to by' });
  await expect(fix).toBeVisible({ timeout: 10000 });
  if (process.env.AMPLE_SHOTS) await page.screenshot({ path: process.env.AMPLE_SHOTS + '/hover-11-quick-fix-for-a-doc-comment.png' });
  await fix.click();
  await expect.poll(() => srcText(page)).toContain('@param by how far');
});

test('the tip of an error has no empty advice, is roomy, and can be resized by its corner (and the size is kept)', async ({ page }) => {
  const song = ['composition "p" { ample 1  tempo 100  master-seed 3 }', '', '/**', ' * @param source the notes', ' * @param byy how far', ' */', 'helper up(Notes source, Interval by) -> Notes {', '  source |> transpose(by)', '}', ''].join('\n');
  await openSong(page, song, 'helper up');
  const line = page.locator('.cm-line', { hasText: 'helper up(' }).first();
  await line.scrollIntoViewIfNeeded();
  const box = await line.boundingBox();
  const open = async () => { await page.locator('#se-prob .seerr').first().dblclick({ timeout: 15000 }); };
  await open();
  const tip = page.locator('#se-errtip');
  await expect(tip).toBeVisible({ timeout: 10000 });
  await expect(tip.locator('.et-hint')).toHaveCount(0);   // "fix the line and the mark goes" says nothing
  await expect(tip).not.toContainText('mark goes');
  expect(await tip.evaluate((e) => getComputedStyle(e).resize)).toBe('both');
  const before = (await tip.boundingBox())!;
  expect(before.width).toBeGreaterThanOrEqual(340);
  // drag the corner: the tip grows, and it comes back that size
  await page.mouse.move(before.x + before.width - 4, before.y + before.height - 4);
  await page.mouse.down();
  await page.mouse.move(before.x + before.width + 80, before.y + before.height + 50, { steps: 6 });
  await page.mouse.up();
  const after = (await tip.boundingBox())!;
  expect(after.width).toBeGreaterThan(before.width + 40);
  expect(after.height).toBeGreaterThan(before.height + 20);
  expect(await page.evaluate(() => localStorage.getItem('ample.errtip.size'))).toContain('"w"');
  await page.mouse.move(5, 5);
  await expect(tip).toBeHidden();
  await open();
  await expect(tip).toBeVisible({ timeout: 10000 });
  expect(Math.abs((await tip.boundingBox())!.width - after.width)).toBeLessThan(3);
});
