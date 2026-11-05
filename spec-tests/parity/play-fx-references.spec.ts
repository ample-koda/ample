import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

test.use({ launchOptions: { args: ['--autoplay-policy=no-user-gesture-required', '--mute-audio'] } });

// A clip used in the notes of another clip (notes part.notes |> shift(0bars)) gets a dotted line and lights only in the bars where the pipeline uses it.
// The master has another name than "master" on purpose: the lights of a song follow the chain that leads into its master.
const SONG = 'composition "demo" { ample 1  tempo 120  master-seed 7 }\nmidi-clip part at 1 for 2 {\n  notes 1:0:1:45:100 2:0:1:48:100\n  bars 2\n}\nmidi-clip whole at 3 for 2 {\n  notes part.notes |> shift(0bars)\n}\ndrums drums at 1 for 2 {\n  pattern beat-A\n}\nbass bass at 3 for 2 {\n  from whole.notes -> notes\n}\nmixer mix {\n  from drums.audio -> audio1\n  from bass.audio -> audio2\n}\nmaster out bake {\n  from mix.audio -> audio\n}\n';

freshServer({ song: SONG });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

test('the used clip and its dotted line are lit in the bars of its use and dark in the others', async ({ page }) => {
  await expect(page.locator('#links path.vis.ref[data-from="part"][data-to="whole"]')).toHaveCount(1);
  const at = (bar: number) => page.evaluate(async (b) => { const s = await import('/app/store.js'); const m = await import('/lib/block-activity.mjs'); const a = m.activity(s.flat(), b); return { nodes: [...a.nodes], ref: a.links.has(m.refKey('part', 'whole')) }; }, bar);
  const early = await at(1);
  expect(early.nodes).not.toContain('part');   // the clip that uses it sits at bars 3 and 4
  expect(early.ref).toBe(false);
  const later = await at(3);
  expect(later.nodes).toContain('part');
  expect(later.ref).toBe(true);
  expect((await at(5)).nodes).not.toContain('part');   // after its two bars it is done
});

test('while it plays, the used clip lights with its line, also when the master is not called "master"', async ({ page }) => {
  await page.getByRole('button', { name: 'Build', exact: true }).click();
  await expect.poll(() => page.evaluate(async () => { const s = await import('/app/store.js'); return Boolean(s.S.build.takes.out); }), { timeout: 40000 }).toBe(true);
  await page.evaluate(async () => { const p = await import('/app/player.js'); const s = await import('/app/store.js'); await p.play(s.S.build.takes.out, 5); });   // bar 3.5: the pipeline uses the clip
  await expect.poll(async () => page.evaluate(() => (window as unknown as { AMPLE_PLAYFX: { state: { nodes: string[] } } }).AMPLE_PLAYFX.state.nodes.includes('part'))).toBe(true);
  await expect(page.locator('#links path.vis.ref.fx-on')).toHaveCount(1);
});
