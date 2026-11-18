import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { freshServerPerTest, openApp, ownServer } from '../../web/tests/support';

// Live Preview in the code editor (plan/live-preview/PLAN.md): listen to the block at the cursor while editing, build after a typing pause, keep the last
// good build playing after a failed one, Stop cancels a running build, bars with a lead-in or a fast draft, and the code that plays lights up.
freshServerPerTest();

const song = `composition "Live preview" { ample 1  tempo 120  master-seed 7 }
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

type Build = { only?: string; range?: { firstBar: number; bars: number }; intent?: string };

async function openSource(page: Page) {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), song);
  const builds: Build[] = [];
  page.on('request', (request) => { if (request.url().endsWith('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON() ?? {}); });
  await page.click('#vs-source');
  await expect(page.locator('#se-listen')).toBeVisible();
  return builds;
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

test('the block at the cursor plays after Ctrl+Shift+Space, built alone at preview quality, and its code lights up', async ({ page }) => {
  const builds = await openSource(page);
  await caretIn(page, 'synth lead', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1 of lead', { timeout: 30000 });
  expect(builds[0]).toMatchObject({ only: 'lead', intent: 'preview' });
  await expect(page.locator('#lp-play')).toHaveText('■ Stop');
  await expect(page.locator('.cm-content .lp-target').first()).toBeAttached();
  await expect(page.locator('.cm-content .lp-now').first()).toBeAttached({ timeout: 10000 });   // a literal note of the theme that feeds lead
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toHaveText('Stopped');
});

test('Space types in the editor; typing builds once after the pause; Ctrl+Shift+Enter does not wait', async ({ page }) => {
  const builds = await openSource(page);
  await caretIn(page, 'synth lead', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1', { timeout: 30000 });
  const text = () => page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => ta.value);
  const before = await text();
  await caretIn(page, 'gain -12 dB', 0);
  await page.locator('#se-cm .cm-content').press('Space');
  expect((await text()).length).toBe(before.length + 1);
  for (const key of ['#', ' ', 'a', 'b', 'c', 'Enter']) await page.locator('#se-cm .cm-content').press(key === ' ' ? 'Space' : key);
  await expect(status(page)).toContainText('hearing build #2', { timeout: 30000 });
  expect(builds.length, 'one build for the whole burst of typing').toBe(2);
  // a change, then Ctrl+Shift+Enter: the build starts without the pause, and only one runs
  await page.locator('#se-cm .cm-content').press('x');
  await page.waitForTimeout(500);   // the text is applied (450 ms), the pause of the session is still waiting
  await page.locator('#se-cm .cm-content').press('Control+Shift+Enter');
  await expect.poll(() => builds.length, { timeout: 2000 }).toBe(3);
  await page.waitForTimeout(1500);
  expect(builds.length).toBe(3);
});

test('a failed build keeps the last good build playing and says so', async ({ page }) => {
  await openSource(page);
  await caretIn(page, 'synth lead', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1', { timeout: 30000 });
  // the next build fails on the server (the request is answered with an error, as a crashed build would be)
  await page.route('**/api/build', (route) => route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'AMP5000 the test made this build fail' }) }));
  await caretIn(page, 'gain -12 dB', 6);
  await page.locator('#se-cm .cm-content').press('4');
  await page.waitForTimeout(500);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Enter');
  await expect(status(page)).toContainText('build failed', { timeout: 30000 });
  await expect(status(page)).toContainText('still hearing build #1');
  await expect(page.locator('#lp-play')).toHaveText('■ Stop');
});

test('Stop while a build runs stops the sound and cancels the build', async ({ page }) => {
  const builds = await openSource(page);
  await caretIn(page, 'synth lead', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1', { timeout: 30000 });
  await caretIn(page, 'gain -12 dB', 6);
  await page.locator('#se-cm .cm-content').press('3');   // -312 dB is clamped, but the text changes
  await page.locator('#se-cm .cm-content').press('Control+Enter');
  await expect(status(page)).toContainText('building', { timeout: 5000 });
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toHaveText('Stopped');
  await expect(page.locator('#lp-play')).toHaveText('▶ Listen');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.S.build.running))).toBe(false);
  expect(builds.length).toBeGreaterThanOrEqual(2);
});

test('bars are built from bar 1 (one bar past), only the chosen bars are downloaded, and the route is said; a fast draft is marked as not exact', async ({ page }) => {
  const builds = await openSource(page);
  const ranges: string[] = [];
  page.on('request', (request) => { if (request.url().includes('/download?format=wav') && request.headers().range) ranges.push(request.headers().range); });
  await page.selectOption('#lp-target', 'bars');
  await page.fill('#lp-first', '3'); await page.locator('#lp-first').dispatchEvent('change');
  await page.fill('#lp-last', '4'); await page.locator('#lp-last').dispatchEvent('change');
  await page.click('#lp-play');
  await expect(status(page)).toContainText('bars 3-4 as in the whole song: built from bar 1 (2 bars before the range rendered, not played)', { timeout: 30000 });
  expect(builds[builds.length - 1]).toMatchObject({ range: { firstBar: 1, bars: 5 }, intent: 'preview', listen: true });
  // the header, then only the bytes of bars 3-4: the buffer that plays is two bars long (120 BPM: 4 s), not the five bars that were built
  // the status names the route as soon as the build is done, while the sound is still loading: wait for the second request and for the buffer
  await expect.poll(() => ranges.length, { timeout: 30000 }).toBe(2);
  expect(ranges[0]).toBe('bytes=0-65535');
  const heardNow = () => page.evaluate(() => import('/app/listen-player.js').then((p) => { const e = p.listenPlayerState().entry; return { duration: e?.buffer?.duration, offset: e?.sliceOffset }; }));
  await expect.poll(async () => (await heardNow()).duration ?? 0, { timeout: 30000 }).toBeGreaterThan(0);
  const heard = await heardNow();
  expect(Math.abs((heard.duration ?? 0) - 4)).toBeLessThan(0.01);
  expect(Math.abs((heard.offset ?? 0) - 4)).toBeLessThan(0.01);
  await page.click('#lp-play');
  await page.locator('.lp-more > summary').click();
  await page.selectOption('#lp-bars-method', 'fast-draft');
  await page.click('#lp-play');
  await expect(status(page)).toContainText('FAST DRAFT: bars 3-4 built alone, not an exact preview', { timeout: 30000 });
  expect(builds[builds.length - 1]).toMatchObject({ range: { firstBar: 3, bars: 3 } });
  await expect(status(page)).toHaveClass(/draft/);
  await page.click('#lp-play');
});

// plan 6.1: a listening build keeps its takes apart; the card players and the transport keep the takes of the normal build
const normalState = (page: Page) => page.evaluate(() => import('/app/store.js').then((s) => JSON.parse(JSON.stringify({
  takes: s.S.build.takes,
  status: s.S.build.status,
  master: s.S.build.master ?? null,
  clockAt: s.S.build.clockAt ?? null,
}))));

test('a listening build does not replace the takes of the normal build, and Live Preview plays its own preview take', async ({ page }) => {
  await openSource(page);
  const listened: string[] = [];
  page.on('response', async (response) => {
    if (!response.url().endsWith('/api/build') || response.request().method() !== 'POST' || response.request().postDataJSON()?.intent !== 'preview') return;
    const report = await response.json().catch(() => null);
    const lead = (report?.blocks || []).find((b: { block: string }) => b.block === 'lead');
    if (lead?.takeId) listened.push(lead.takeId);
  });
  // a normal build first: its takes are what the cards and the transport play
  await page.evaluate(() => import('/app/store.js').then((s) => s.build()));
  const before = await normalState(page);
  expect(before.takes.lead, 'the normal build made a take of lead').toBeTruthy();
  await caretIn(page, 'synth lead', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1 of lead', { timeout: 30000 });
  expect(listened.length).toBe(1);
  expect(listened[0], 'the preview take is another take than the full-quality one').not.toBe(before.takes.lead);
  expect(await normalState(page), 'takes, status, master and clock of the normal build are untouched').toEqual(before);
  const playing = await page.evaluate(() => import('/app/listen-player.js').then((p) => p.listenPlayerState().entry?.take ?? null));
  expect(playing, 'Live Preview plays the take of its own build').toBe(listened[0]);
  // a whole-song listening build does not empty the status of the normal build either
  await page.selectOption('#lp-target', 'song');
  await expect(status(page)).toContainText('of the song', { timeout: 30000 });
  expect(await normalState(page)).toEqual(before);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
});

test('a block with several audio outputs gets an output choice, and the chosen output plays', async ({ page }) => {
  await openApp(page, { reset: false });
  const split = song.replace(
    'master out {\n  from lead.audio -> audio\n}',
    'splitter split {\n  from lead.audio -> audio\n}\nmixer mix {\n  from split.left -> audio1\n  from split.right -> audio2\n}\nmaster out {\n  from mix.audio -> audio\n}',
  );
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), split);
  await page.click('#vs-source');
  await caretIn(page, 'splitter split', 4);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1 of split.left', { timeout: 30000 });
  await expect(page.locator('#lp-port')).toBeVisible();
  await page.selectOption('#lp-port', 'right');
  await expect(status(page)).toContainText('of split.right', { timeout: 30000 });
  await expect.poll(() => page.evaluate(() => import('/app/listen-player.js').then((p) => p.listenPlayerState().entry?.role ?? null)), { timeout: 15000 }).toBe('right');
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
});

// plan 6.2: a listening build asks the server to leave the song state alone (listen: true), and events of another page's listening build change nothing here
test('a listening build leaves the song clock alone, and listen events of another page do not change the build state', async ({ page }) => {
  const builds = await openSource(page);
  await page.evaluate(() => import('/app/store.js').then((s) => s.build()));
  const clock = () => page.evaluate(async () => {
    const [{ S }, { api }] = await Promise.all([import('/app/store.js'), import('/app/api.js')]);
    const doc = await api.get(`/api/doc?path=${encodeURIComponent(S.path)}`);
    return { clockAt: doc.clockAt, builtClock: doc.builtClock };
  });
  const before = await clock();
  expect(before.builtClock).toBe(true);
  await page.waitForTimeout(1100);   // the clock has seconds: a build now would move it
  await caretIn(page, 'synth lead', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1 of lead', { timeout: 30000 });
  expect(builds[builds.length - 1]).toMatchObject({ only: 'lead', intent: 'preview', listen: true });
  expect(await clock(), 'the song clock (piano roll, baking of dynamic values) is the one of the normal build').toEqual(before);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  // another page's listening build: its progress events do not become the build state of this page
  const after = await page.evaluate(() => import('/app/store.js').then((s) => {
    const was = JSON.stringify(s.S.build.status);
    s.onBuildProgress({ block: 'lead', status: 'rendered', buildId: 'other-page', listen: true });
    s.onBlockProgress({ block: 'lead', state: 'running', fraction: 0.5, buildId: 'other-page', listen: true });
    const same = JSON.stringify(s.S.build.status) === was, live = Boolean(s.S.build.live?.lead);
    s.onBuildProgress({ block: 'theme', status: 'waiting', buildId: 'other-normal' });   // a normal build of another page is shown as before
    return { same, live, normal: s.S.build.status.theme?.status ?? null };
  }));
  expect(after).toEqual({ same: true, live: false, normal: 'waiting' });
});

// WP6: the chord of the bar lights up while the synth it feeds plays, and the drum section of the bar (engine timing, plan 3.5)
test('the chord that sounds and the drum section of the bar light up', async ({ page }) => {
  await openApp(page, { reset: false });
  const words = `composition "Chords" { ample 1  tempo 120  master-seed 7 }
midi-clip prog at 1 for 4 {
  chords Am F C G
}
synth pad {
  wave triangle
  gain -12 dB
  from prog.notes -> notes
}
drums beat at 1 for 4 {
  kit builtin
  pattern beat-A
  song pattern beat-B
}
mixer mix {
  from pad.audio -> audio1
  from beat.audio -> audio2
}
master out {
  from mix.audio -> audio
}
`;
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), words);
  await page.click('#vs-source');
  await caretIn(page, 'synth pad', 6);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1 of pad', { timeout: 30000 });
  await expect.poll(async () => (await page.locator('.cm-content .lp-now').allTextContents()).join(' '), { timeout: 10000 }).toMatch(/^(Am|F|C|G)$/);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await caretIn(page, 'drums beat', 6);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #', { timeout: 30000 });
  await expect.poll(async () => (await page.locator('.cm-content .lp-now').allTextContents()).join(' '), { timeout: 10000 }).toMatch(/\b(pattern|beat-B)\b/);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
});

// plan 6.3: a listening build never writes ample.lock; a component that is not pinned yet is said in the status, a normal build pins it
test('a component that is not pinned is said in the status, and listening does not write ample.lock', async ({ page }) => {
  const folder = ownServer()!.folder, lock = path.join(folder, 'ample.lock');
  fs.mkdirSync(path.join(folder, 'components'), { recursive: true });
  const twice = [
    'component demo.fx.twice version 1.0.0 {',
    '  id "com.demo.twice"',
    '  input source: Notes',
    '  property copies: Int = 2',
    '  output notes: Notes',
    '  process {',
    '    for i in 0..copies {',
    '      emit source |> shift(i * 1bar)',
    '    }',
    '  }',
    '}',
    '',
  ].join('\n');
  fs.writeFileSync(path.join(folder, 'components', 'twice.amplec'), twice);
  const before = fs.existsSync(lock) ? fs.readFileSync(lock, 'utf8') : null;
  await openApp(page, { reset: false });
  const text = song.replace('midi-clip theme', 'import "components/twice.amplec"\nmidi-clip theme').replace('from theme.notes -> notes', 'from twice.notes -> notes')
    .replace('synth lead {', 'demo.fx.twice twice {\n  from theme.notes -> source\n}\nsynth lead {');
  await page.evaluate((t) => import('/app/store.js').then((s) => s.applyText(t)), text);
  await page.click('#vs-source');
  await caretIn(page, 'synth lead', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1 of lead', { timeout: 30000 });
  await expect(status(page)).toContainText('not pinned: com.demo.twice@1.0.0');
  expect(fs.existsSync(lock) ? fs.readFileSync(lock, 'utf8') : null, 'the listening build left ample.lock as it was').toBe(before);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  // control: a normal build pins it
  await page.evaluate(() => import('/app/store.js').then((s) => s.build()));
  expect(fs.readFileSync(lock, 'utf8')).toContain('com.demo.twice@1.0.0');
});

// the bar is one line that never changes height while the status changes, so the editor below it does not jump while typing (owner report, 2026-11-17)
test('the Live Preview bar keeps one height while listening and typing', async ({ page }) => {
  await page.setViewportSize({ width: 1300, height: 800 });
  await openSource(page);
  const bar = page.locator('#se-listen'), editorTop = () => page.locator('#se-cm').evaluate((e) => Math.round(e.getBoundingClientRect().top));
  const height = () => bar.evaluate((e) => Math.round(e.getBoundingClientRect().height));
  const idle = await height();
  expect(idle).toBeLessThanOrEqual(40);
  await caretIn(page, 'synth lead', 8);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
  await expect(status(page)).toContainText('hearing build #1', { timeout: 30000 });
  const top = await editorTop();   // the editor is laid out by now (its first position is measured before CodeMirror is ready)
  const seen = new Set<number>(), tops = new Set<number>();
  await caretIn(page, 'gain -12 dB', 0);
  for (let i = 0; i < 12; i++) {
    await page.locator('#se-cm .cm-content').press(i % 2 ? 'Backspace' : 'x');
    await page.waitForTimeout(250);
    seen.add(await height()); tops.add(await editorTop());
  }
  expect([...seen], 'the bar height while the status changes').toEqual([idle]);
  expect([...tops], 'the editor does not move').toEqual([top]);
  await page.locator('#se-cm .cm-content').press('Control+Shift+Space');
});
