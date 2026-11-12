import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();

function wavFixture() {
  const rate = 44_100, frames = rate * 2, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24);
  bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let frame = 0; frame < frames; frame++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * frame / rate) * 9000), 44 + frame * 2);
  return bytes;
}

async function connect(page, fromId: string, toId: string, inPort = 0) {
  const from = page.locator(`.node[data-id="${fromId}"] .port.out`).first();
  const to = page.locator(`.node[data-id="${toId}"] .port.in`).nth(inPort);
  let a!: { x: number; y: number; width: number; height: number }, b!: typeof a;
  await expect(async () => {
    await from.scrollIntoViewIfNeeded(); await to.scrollIntoViewIfNeeded();
    const first = (await from.boundingBox())!, second = (await to.boundingBox())!;
    await page.waitForTimeout(150);
    const again = (await from.boundingBox())!, secondAgain = (await to.boundingBox())!;
    expect(again.x).toBe(first.x); expect(again.y).toBe(first.y); expect(secondAgain.x).toBe(second.x); expect(secondAgain.y).toBe(second.y);
    a = again; b = secondAgain;
  }).toPass({ timeout: 15000 });
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2); await page.mouse.down();
  await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 4 }); await page.mouse.up();
}

test('@audio the live chain loops a built source through several effects, with a bypass per block and a preview that is heard before it is written', async ({ page }) => {
  await page.addInitScript(() => {
    (window as any).__params = 0;
    const original = AudioParam.prototype.setTargetAtTime;
    AudioParam.prototype.setTargetAtTime = function (...args) { (window as any).__params++; return original.apply(this, args); };
  });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src');
  await addTemplateBlock(page, 'eq', 'tone');
  await addTemplateBlock(page, 'reverb', 'room');
  await connect(page, 'src', 'tone'); await connect(page, 'tone', 'room');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.filter((l) => l.type === 'audio').length))).toBeGreaterThanOrEqual(2);
  const wav = wavFixture();
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: wav }));
  // the source counts as built for the whole test (the build status of the server would clear a take set by hand); the room is built only when the test says so
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'src' ? 'take-src' : key === 'room' && (window as any).__roomBuilt ? 'take-room' : undefined) }), set: () => {} }); }));

  const started = await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('room'));
  expect(started).toBe(true);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.active).toBe(true);
  expect(state.items.map((i) => i.id)).toEqual(['src', 'tone', 'room']);
  await expect(page.locator('.livechain')).toContainText('Live chain');
  await expect(page.locator('.livechain .lcchip')).toHaveCount(2);
  await expect(page.locator('.livechain .lclatency')).toContainText(/^Latency [0-9]/);

  // the chain really sounds: the room (the last block) has a level above silence
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('room')?.left ?? 0)), { timeout: 8000 }).toBeGreaterThan(0.01);
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing)).toBe(true);

  // a preview moves audio params at once, the song text is not written
  const before = await page.evaluate(() => (window as any).__params);
  const versionBefore = await page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version));
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainPreview('tone', 'high', '9')));
  expect(await page.evaluate(() => (window as any).__params)).toBeGreaterThan(before);
  expect(await page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version))).toBe(versionBefore);

  // bypass: the button says so and the block stays in the chain
  await page.locator('.livechain .lcchip[data-block="tone"] .lcbypass').click();
  await expect(page.locator('.livechain .lcchip[data-block="tone"] .lcbypass')).toHaveAttribute('aria-pressed', 'true');
  expect((await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()))).items.find((i) => i.id === 'tone')?.bypassed).toBe(true);

  // A/B against the build: the built take of the last block takes the place of the chain and back
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).setCompareBuilt(true))).toBe(null);   // nothing built for the room yet
  await page.evaluate(() => { (window as any).__roomBuilt = true; });
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).setCompareBuilt(true))).toBe(true);
  await expect(page.locator('.livechain .lccompare')).toHaveAttribute('aria-pressed', 'true');
  await page.locator('.livechain .lccompare').click();
  await expect(page.locator('.livechain .lccompare')).toHaveAttribute('aria-pressed', 'false');

  await page.locator('.livechain .lcstop').click();
  await expect(page.locator('.livechain')).toHaveCount(0);
  expect((await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()))).active).toBe(false);
});

test('@audio a chain that starts from an unbuilt source says what to build', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src');
  await addTemplateBlock(page, 'delay', 'echo');
  await connect(page, 'src', 'echo');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.some((l) => l.to === 'echo')))).toBe(true);
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('echo'))).toBe(false);
  await expect(page.locator('body')).toContainText('Build src first');
});

test('@audio built-in drums and a buffer operation are made in the browser without a build, and a knob re-makes them and swaps them in', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'beat');
  await addTemplateBlock(page, 'reverse', 'back');
  await addTemplateBlock(page, 'reverb', 'room');
  for (const [name, value] of [['kit', 'builtin']]) {
    const doc = await api('/api/doc?path=song.amplet');
    await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'setParameter', block: 'beat', name, value } } });
  }
  await page.reload(); await page.waitForSelector('body[data-ready="1"]');
  await connect(page, 'beat', 'back'); await connect(page, 'back', 'room');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.filter((l) => l.type === 'audio').length))).toBeGreaterThanOrEqual(2);
  // nothing is built: the drums come from the browser's exact renderer
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('room'))).toBe(true);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.items.map((i) => `${i.id}:${i.kind}`)).toEqual(['beat:local', 'back:local', 'room:effect']);
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('room')?.left ?? 0)), { timeout: 10000 }).toBeGreaterThan(0.005);
  // a knob of the drums is heard after a short re-render, swapped in once
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainPreview('beat', 'pattern', 'beat-B')));
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState().swaps)), { timeout: 10000 }).toBeGreaterThanOrEqual(2);   // the drums, then the reverse after them
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});

test('@audio the Advanced window says for every parameter whether it moves live, re-renders or needs a build', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'convolution', 'echo');
  await page.locator('.node[data-id="echo"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  await expect(dialog.locator('.live-badge').first()).toBeVisible();
  const badges = await dialog.locator('.live-badge').allTextContents();
  expect(badges).toContain('live');
  expect(badges).toContain('re-renders');   // length and normalize rebuild the impulse
});
