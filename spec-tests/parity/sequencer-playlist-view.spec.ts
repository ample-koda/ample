import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The playlist of the sequencer: Fit zooms so the whole song is in view, the clips of the chosen pattern stand out, and every track has its number (an empty one is lower).
const SEQ = 'sequencer 1\nchannel lead notes wave=square\npattern verse bars=4\n  lead 1:0:1:60:96 1:1:1:64:96\npattern chorus bars=4\n  lead 1:0:1:72:96\nplaylist\n  clip verse track=1 at=1 for=4\n  clip chorus track=1 at=5 for=4\n  clip verse track=1 at=9 for=4\n  clip chorus track=1 at=13 for=4\n  clip verse track=1 at=17 for=4\n  clip chorus track=1 at=21 for=4\n  clip verse track=1 at=25 for=4\n  clip chorus track=1 at=29 for=4\n  clip verse track=1 at=33 for=4\n  clip chorus track=1 at=37 for=4\n  clip verse track=1 at=41 for=4\n  clip chorus track=1 at=45 for=4\n';
freshServer({
  song: 'composition "band" { ample 1  tempo 120  master-seed 3 }\nsequencer sq at 1 for 48 {\n  file sequences/a.seq\n  lanes lane1=lead\n}\n',
  files: { 'sequences/a.seq': SEQ },
});
test.beforeEach(async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq')).toBeVisible();
  await expect(page.locator('#sq .sqclip')).toHaveCount(12);
});

test('Fit puts the whole song in view without scrolling sideways', async ({ page }) => {
  const scrolls = () => page.locator('#sq-playlist .sqscroll').evaluate((el) => el.scrollWidth - el.clientWidth);
  expect(await scrolls()).toBeGreaterThan(0);   // 52 bars at the normal zoom do not fit
  await page.locator('#sq-zoom-fit').click();
  await expect.poll(scrolls).toBeLessThanOrEqual(2);
});

test('the clips of the chosen pattern stand out and the others are dimmed', async ({ page }) => {
  const chosen = () => page.locator('#sq .sqclip.ofchosen').evaluateAll((clips) => clips.map((c) => c.textContent));
  expect(await chosen()).toEqual(Array(6).fill('verse'));
  await page.locator('#sq-patterns .sqtab[data-pattern="chorus"]').click();
  await expect.poll(chosen).toEqual(Array(6).fill('chorus'));
  const opacities = await page.locator('#sq .sqclip').evaluateAll((clips) => clips.map((c) => ({ chosen: c.classList.contains('ofchosen'), opacity: Number(getComputedStyle(c).opacity) })));
  for (const o of opacities) expect(o.opacity).toBeCloseTo(o.chosen ? 1 : 0.62, 1);
});

test('every track has its number, and a track without clips is lower', async ({ page }) => {
  const tracks = await page.locator('#sq .sqtrack').evaluateAll((lanes) => lanes.map((l) => ({ number: getComputedStyle(l, '::before').content, empty: l.classList.contains('empty'), height: l.getBoundingClientRect().height })));
  expect(tracks.map((t) => t.number)).toEqual(['"1"', '"2"', '"3"', '"4"']);
  expect(tracks[0].empty).toBe(false);
  expect(tracks.slice(1).every((t) => t.empty)).toBe(true);
  expect(tracks[1].height).toBeLessThan(tracks[0].height);
});
