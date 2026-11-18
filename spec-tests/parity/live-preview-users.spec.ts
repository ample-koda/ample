import { test, expect, type Page } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

// Live Preview: several chains read one melody. Each user of the source gets a colour (and a number and a shape), the sounding notes of the source are lit per
// user, a "used by" list names them (hover isolates one, click jumps to its clip), and more than four users share a neutral tint (plan/live-preview/note-origin.md).
freshServerPerTest();

const NAMES = ['high', 'low', 'echo', 'bass', 'pad', 'lead'];
const SHIFTS = ['transpose(+12)', 'transpose(-12)', 'shift(+1beat)', 'transpose(-7)', 'transpose(+7)', 'shift(+2beats)'];

function songOf(count: number) {
  const names = NAMES.slice(0, count);
  const clips = names.map((n, i) => `midi-clip ${n} at 1 for 2 {\n  notes theme.notes |> ${SHIFTS[i]}\n}\n`).join('');
  const synths = names.map((n) => `synth ${n}Synth at 1 for 2 {\n  wave triangle\n  gain -18 dB\n  from ${n}.notes -> notes\n}\n`).join('');
  const inputs = names.map((n, i) => `  from ${n}Synth.audio -> audio${i + 1}\n`).join('');
  return `composition "Users" { ample 1  tempo 120  master-seed 3 }
midi-clip theme at 1 for 2 {
  melody "c4:1 d4:1 e4:1 g4:1 a4:1 g4:1 e4:1 d4:1"
}
${clips}${synths}mixer mix {
  inputs ${count}
  gains "${names.map(() => '0').join(' ')}"
${inputs}}
master out {
  from mix.audio -> audio
}
`;
}

async function listen(page: Page, count: number) {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), songOf(count));
  await page.click('#vs-source');
  await expect(page.locator('#se-listen')).toBeVisible();
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => {   // the caret in the master: its upstream is every clip
    const i = ta.value.indexOf('master out') + 8;
    ta.focus();
    ta.setSelectionRange(i, i);
    ta.onselectionchange?.(new Event('selectionchange'));
  });
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(page.locator('#lp-play')).toHaveText('■ Stop');
  await expect(page.locator('#lp-users')).toBeVisible({ timeout: 90000 });   // the first build is heard and its origins are in
}

test('three chains on one melody: a colour, a number and a name each, notes lit per user, hover isolates and click jumps', async ({ page }) => {
  await listen(page, 3);
  const used = page.locator('#lp-users');
  await expect(used).toBeVisible();
  await expect(used.locator('.lp-source')).toHaveCount(1);
  await expect(used.locator('.lp-source b')).toHaveText('theme');
  const buttons = used.locator('.lp-user');
  await expect(buttons).toHaveCount(3);
  const texts = (await buttons.allTextContents()).map((t) => t.trim()).sort();
  expect(texts.map((t) => t.split(' ')[1]).sort()).toEqual(['echo', 'high', 'low']);
  expect(new Set(texts.map((t) => t.split(' ')[0]))).toEqual(new Set(['1', '2', '3']));   // a number as well as a colour
  const classes = await buttons.evaluateAll((list) => list.map((b) => [...b.classList].find((c) => /^lp-user-/.test(c))));
  expect(new Set(classes).size).toBe(3);
  await expect(buttons.first()).toHaveAttribute('title', /(transpose|shift)/);   // what the chain does

  // the notes of the melody that sound are lit, each with the number of its user
  await expect(page.locator('.cm-content .lp-now[data-lp-name]').first()).toBeAttached({ timeout: 15000 });
  await expect.poll(async () => page.locator('.cm-content .lp-now[data-lp-name]').evaluateAll((l) => new Set(l.map((x) => x.getAttribute('data-lp-name'))).size), { timeout: 20000 }).toBeGreaterThanOrEqual(2);

  // hovering a user keeps only its notes
  await buttons.filter({ hasText: 'high' }).hover();
  await expect.poll(async () => page.locator('.cm-content .lp-now[data-lp-name]').evaluateAll((l) => [...new Set(l.map((x) => x.getAttribute('data-lp-name')))]), { timeout: 10000 }).toEqual(['high']);
  await page.mouse.move(5, 5);
  await expect.poll(async () => page.locator('.cm-content .lp-now[data-lp-name]').evaluateAll((l) => new Set(l.map((x) => x.getAttribute('data-lp-name'))).size), { timeout: 10000 }).toBeGreaterThanOrEqual(1);

  // a click jumps to the clip of that user
  await buttons.filter({ hasText: 'echo' }).click();
  const caretLine = await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => ta.value.slice(ta.value.lastIndexOf('\n', ta.selectionStart - 1) + 1, ta.value.indexOf('\n', ta.selectionStart)));
  expect(caretLine).toContain('midi-clip echo');
});

test('six chains on one melody: four colours, the other two share a neutral tint and are counted', async ({ page }) => {
  await listen(page, 6);
  const buttons = page.locator('#lp-users .lp-user');
  await expect(buttons).toHaveCount(6);
  const labels = (await buttons.allTextContents()).map((t) => t.trim().split(' ')[0]).sort();
  expect(labels).toEqual(['+', '+', '1', '2', '3', '4']);
  await expect(page.locator('#lp-users .lp-user-n')).toHaveCount(2);
  await expect(page.locator('#lp-users')).toContainText('2 without a colour');
  for (const slot of [1, 2, 3, 4]) await expect(page.locator(`#lp-users .lp-user-${slot}`)).toHaveCount(1);
});
