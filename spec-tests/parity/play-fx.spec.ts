import { test, expect, type Page } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The play lights (plan/play-fx): while a built song plays and is still the song in the studio, the blocks that sound glow and the lines carry the signal.
const SONG = 'composition "demo" { ample 1  tempo 120  master-seed 7 }\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\ndrums drums at 1 for 2 {\n  pattern beat-A\n}\nbass bass at 3 for 2 {\n  from chords.notes -> notes\n}\npiano piano pinned@2.0 at 3 for 2 {\n  from chords.notes -> notes\n}\nmixer mix {\n  from drums.audio -> audio1\n  from bass.audio -> audio2\n  from piano.audio -> audio3\n}\nmaster master bake {\n  from mix.audio -> audio\n}\n';

test.use({ launchOptions: { args: ['--autoplay-policy=no-user-gesture-required', '--mute-audio'] } });
freshServer({ song: SONG });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

const lit = (page: Page) => page.evaluate(() => (window as unknown as { AMPLE_PLAYFX: { state: { active: boolean; nodes: string[]; links: string[] } } }).AMPLE_PLAYFX.state);
const playFrom = (page: Page, seconds: number) => page.evaluate(async (from) => { const p = await import('/app/player.js'); const s = await import('/app/store.js'); await p.play(s.S.build.takes.master, from); }, seconds);

async function built(page: Page) {
  await page.getByRole('button', { name: 'Build', exact: true }).click();
  await expect.poll(() => page.evaluate(async () => { const s = await import('/app/store.js'); return Boolean(s.S.build.takes.master); }), { timeout: 60000 }).toBe(true);
}

test('the blocks and lines that sound light up with the parts, and everything goes dark on stop', async ({ page }) => {
  await built(page);
  await playFrom(page, 0.2);
  await expect.poll(async () => (await lit(page)).active).toBe(true);
  const first = await lit(page);
  expect(first.nodes).toContain('master');
  expect(first.nodes).toContain('mix');
  expect(first.nodes).not.toContain('bass');   // the bass part starts at bar 3
  await expect(page.locator('#world .node.fx-on').first()).toBeVisible();
  await expect(page.locator('#links path.vis.fx-on').first()).toBeVisible();
  await playFrom(page, 5);   // bar 3.5: the drums are done, the bass and the piano are in
  await expect.poll(async () => (await lit(page)).nodes.includes('bass') || (await lit(page)).nodes.includes('piano')).toBe(true);
  expect((await lit(page)).nodes).not.toContain('drums');
  await page.evaluate(async () => { const p = await import('/app/player.js'); p.stop(); });
  await expect.poll(async () => (await lit(page)).nodes.length).toBe(0);
  await expect(page.locator('#world .node.fx-on')).toHaveCount(0);
});

test('an edit while it plays puts the lights out, because what plays is no longer the song', async ({ page }) => {
  await built(page);
  await playFrom(page, 0.2);
  await expect.poll(async () => (await lit(page)).active).toBe(true);
  await page.evaluate(async () => { const s = await import('/app/store.js'); await s.edit([{ type: 'addBlock', blockType: 'midi-clip', name: 'extra' }]); });
  await expect.poll(async () => (await lit(page)).active).toBe(false);
  await expect(page.locator('#world .node.fx-on')).toHaveCount(0);
});

test('the Lights switch turns the play lights off and on', async ({ page }) => {
  await built(page);
  const lights = page.getByRole('button', { name: 'Lights' });
  await expect(lights).toHaveAttribute('aria-pressed', 'true');
  await lights.click();
  await expect(lights).toHaveAttribute('aria-pressed', 'false');
  await playFrom(page, 0.2);
  await page.waitForTimeout(800);
  await expect(page.locator('#world .node.fx-on')).toHaveCount(0);
  await lights.click();
  await expect.poll(async () => (await lit(page)).active).toBe(true);
});

test('every lit block has its own scrubber that runs through its own waveform while the song plays', async ({ page }) => {
  await built(page);
  await playFrom(page, 0.2);
  await expect.poll(async () => (await lit(page)).nodes.includes('drums')).toBe(true);
  const head = () => page.evaluate(() => parseFloat((document.querySelector('.node[data-id="drums"] .plhead') as HTMLElement | null)?.style.left || '0'));
  await expect.poll(head).toBeGreaterThan(0);
  const first = await head();
  await expect.poll(head).toBeGreaterThan(first);
});

test('a local preview in the browser lights the blocks too, from the first bar, and goes dark when it stops', async ({ page }) => {
  await page.evaluate(async () => {
    const rate = 8000, seconds = 8, data = new Uint8Array(rate * seconds * 2), header = new DataView(new ArrayBuffer(44));
    const text = (at: number, s: string) => { for (let i = 0; i < s.length; i++) header.setUint8(at + i, s.charCodeAt(i)); };
    text(0, 'RIFF'); header.setUint32(4, 36 + data.length, true); text(8, 'WAVE'); text(12, 'fmt '); header.setUint32(16, 16, true); header.setUint16(20, 1, true); header.setUint16(22, 1, true);
    header.setUint32(24, rate, true); header.setUint32(28, rate * 2, true); header.setUint16(32, 2, true); header.setUint16(34, 16, true); text(36, 'data'); header.setUint32(40, data.length, true);
    const p = await import('/app/player.js');
    await p.playTransient(new Blob([header.buffer, data], { type: 'audio/wav' }));
  });
  await expect.poll(async () => (await lit(page)).active).toBe(true);
  const state = await lit(page);
  expect(state.nodes).toContain('master');
  expect(state.nodes).toContain('drums');
  expect(state.nodes).not.toContain('bass');   // bar 1 to 2: the bass part starts at bar 3
  await page.evaluate(async () => { const p = await import('/app/player.js'); p.stop(); });
  await expect.poll(async () => (await lit(page)).nodes.length).toBe(0);
});
