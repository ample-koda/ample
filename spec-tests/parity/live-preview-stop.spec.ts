import { test, expect, type Page, type Route } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

// Live Preview: Stop reaches every point where a late task could still act. The requests are held by the test (the take download, the normal build), so the
// order of events is fixed: Stop while the sound loads, Stop then Listen again, and Stop while Live Preview waits for a normal build.
freshServerPerTest();

const song = `composition "Live preview stop" { ample 1  tempo 120  master-seed 7 }
midi-clip theme at 1 for 4 {
  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100 2:0:2:72:100 3:0:1:60:100 4:0:4:64:100
}
synth lead {
  wave triangle
  gain -12 dB
  from theme.notes -> notes
}
master out {
  from lead.audio -> audio
}
`;

const DOWNLOAD = /\/api\/takes\/[^/]+\/download/;

async function openSource(page: Page) {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), song);
  await page.click('#vs-source');
  await expect(page.locator('#se-listen')).toBeVisible();
}

async function caretIn(page: Page, needle: string, plus = 2) {
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement, [n, p]) => {
    const i = ta.value.indexOf(n as string) + (p as number);
    ta.focus();
    ta.setSelectionRange(i, i);
    ta.onselectionchange?.(new Event('selectionchange'));
  }, [needle, plus]);
}

const status = (page: Page) => page.locator('#lp-status');
const player = (page: Page) => page.evaluate(() => import('/app/listen-player.js').then((m) => {
  const s = m.listenPlayerState();
  return { playing: s.playing, pending: s.pending, loading: s.loading };
}));
const toggle = (page: Page) => page.locator('#se-cm .cm-content').press('Control+Shift+Space');

/** Holds every take download until the test lets it go; resolves with the list of held requests. */
async function holdDownloads(page: Page) {
  const held: Route[] = [];
  await page.route(DOWNLOAD, (route) => { held.push(route); });
  return held;
}

async function release(page: Page, route: Route) {
  const answered = page.waitForResponse((r) => DOWNLOAD.test(r.url()));
  await route.continue();
  await answered;
  await page.waitForTimeout(800);   // the sound is decoded and the old task has had every chance to act
}

test('Stop while the sound still loads: when it arrives nothing plays and the session stays stopped', async ({ page }) => {
  await openSource(page);
  const held = await holdDownloads(page);
  await caretIn(page, 'synth lead', 8);
  await toggle(page);
  await expect.poll(() => held.length, { timeout: 30000 }).toBe(1);   // the build is done and the take is loading
  expect((await player(page)).loading).toBe(true);
  await toggle(page);   // Stop
  await expect(status(page)).toHaveText('Stopped');
  expect((await player(page)).loading, 'Stop ends the loading state').toBe(false);
  await release(page, held[0]);
  expect(await player(page)).toMatchObject({ playing: false, pending: false });
  await expect(status(page)).toHaveText('Stopped');
  await expect(page.locator('#lp-play')).toHaveText(/Listen/);
});

test('Stop, then Listen again while the old sound loads: the old one never plays or queues, the new session is untouched', async ({ page }) => {
  await openSource(page);
  const held = await holdDownloads(page);
  await caretIn(page, 'synth lead', 8);
  await toggle(page);
  await expect.poll(() => held.length, { timeout: 30000 }).toBe(1);
  await toggle(page);   // Stop
  await expect(status(page)).toHaveText('Stopped');
  await toggle(page);   // Listen again: a new session builds and loads its own take
  await expect.poll(() => held.length, { timeout: 30000 }).toBe(2);
  await release(page, held[1]);   // the new take arrives first and plays
  await expect(status(page)).toContainText('hearing build #2');
  expect(await player(page)).toMatchObject({ playing: true, pending: false });
  await release(page, held[0]);   // the old take arrives last
  expect(await player(page), 'the old take was neither played nor queued behind the new one').toMatchObject({ playing: true, pending: false });
  await expect(status(page)).toContainText('hearing build #2');
});

test('Stop while Live Preview waits for a normal build: the normal build runs on and the waiting task starts no build', async ({ page }) => {
  await openSource(page);
  const builds: string[] = [];
  let hold: Route | null = null;
  await page.route('**/api/build', async (route) => {
    builds.push(route.request().postData() ?? '');
    if (!hold && builds.length === 1) { hold = route; return; }   // the normal build is held
    await route.continue();
  });
  // a normal build (the Build button's path), started by the test and held in the middle
  await page.evaluate(() => { (window as unknown as { normal: Promise<unknown> }).normal = import('/app/store.js').then((s) => s.build()); });
  await expect.poll(() => builds.length, { timeout: 15000 }).toBe(1);
  await caretIn(page, 'synth lead', 8);
  await toggle(page);   // Listen: it has to wait for the normal build
  await page.waitForTimeout(500);
  expect(builds.length, 'the listening build did not start next to the normal build').toBe(1);
  await toggle(page);   // Stop while it waits
  await expect(status(page)).toHaveText('Stopped');
  await page.waitForTimeout(300);
  await (hold as unknown as Route).continue();   // the normal build goes on
  const report = await page.evaluate(() => (window as unknown as { normal: Promise<unknown> }).normal);
  expect(report, 'the normal build finished and was not cancelled by Stop').toBeTruthy();
  await page.waitForTimeout(1000);   // a withdrawn waiter that was not withdrawn would start its build now
  expect(builds.length, 'no listening build started after Stop').toBe(1);
  const state = await page.evaluate(() => import('/app/store.js').then((s) => ({ error: s.S.build.error || null, running: s.S.build.running, takes: Object.keys(s.S.build.takes || {}).length })));
  expect(state).toMatchObject({ error: null, running: false });
  expect(state.takes, 'the normal build wrote its takes').toBeGreaterThan(0);
  await expect(status(page)).toHaveText('Stopped');
});
