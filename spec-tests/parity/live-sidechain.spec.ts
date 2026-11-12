import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 2.4: the live compressor has a second input, the sidechain key. A click train on the key makes the gain of a steady program dip at the clicks;
// without the key the program is its own detector and stays untouched.
freshServer();
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


test('the key input drives the reduction of the live compressor', async ({ page }) => {
  await openApp(page, { reset: false });
  const r = await page.evaluate(async () => {
    const { liveEffectGraph, prepareWorklets } = await import('/app/studio/preview/audio.js');
    const rate = 48000, frames = rate * 2;
    const run = async (useKey: boolean) => {
      const context = new OfflineAudioContext(2, frames, rate); await prepareWorklets(context);
      const program = context.createBuffer(2, frames, rate), key = context.createBuffer(2, frames, rate);
      const tone = Float32Array.from({ length: frames }, (_, i) => .1 * Math.sin(2 * Math.PI * 220 * i / rate));   // -20 dB: below the threshold on its own
      // a click every 0.5 s: 20 ms of a loud 1 kHz tone
      const clicks = Float32Array.from({ length: frames }, (_, i) => ((i % (rate / 2)) < rate * .02 ? .9 * Math.sin(2 * Math.PI * 1000 * i / rate) : 0));
      for (let c = 0; c < 2; c++) { program.copyToChannel(tone, c); key.copyToChannel(clicks, c); }
      const source = context.createBufferSource(), keySource = context.createBufferSource(), out = context.createGain(); source.buffer = program; keySource.buffer = key; out.connect(context.destination);
      const graph = liveEffectGraph(context, source, out, 'compressor', { threshold: '-18 dB', ratio: '20', attack: '1 ms', release: '150 ms', makeup: '0 dB', keyFilter: '0 Hz', enabled: 'on' }, 120, null);
      if (useKey) { keySource.connect(graph.keyInput); graph.setKey(true); }
      // the message to the worklet needs a moment of real time to arrive before the render starts
      context.suspend(0).then(async () => { await new Promise((resolve) => setTimeout(resolve, 100)); context.resume(); });
      source.start(); keySource.start();
      const result = await context.startRendering(); graph.dispose();
      const left = result.getChannelData(0), rms = (from: number, to: number) => { let s = 0; for (let i = from; i < to; i++) s += left[i] * left[i]; return Math.sqrt(s / (to - from)); };
      const at = (second: number, a: number, b: number) => rms(Math.round((second + a) * rate), Math.round((second + b) * rate));
      return { atClick: at(1, .03, .08), between: at(1, .3, .45), steady: at(.25, .0, .2) };
    };
    return { withKey: await run(true), withoutKey: await run(false), reference: 0.1 / Math.SQRT2 };
  });
  expect(r.withKey.between, 'between the clicks the program is back at its level').toBeGreaterThan(r.reference * .85);
  expect(r.withKey.atClick, 'just after a click the key has pulled the program down').toBeLessThan(r.withKey.between * .5);
  expect(r.withoutKey.atClick, 'without the key nothing is reduced').toBeGreaterThan(r.reference * .95);
});

test('a compressor with a key block plays as a live chain with the key take built in', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src'); await addTemplateBlock(page, 'drums', 'kick'); await addTemplateBlock(page, 'compressor', 'duck');
  await connect(page, 'src', 'duck', 0); await connect(page, 'kick', 'duck', 1);
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.filter((l) => l.type === 'audio' && l.inName === 'key').length))).toBe(1);
  const wav = wavFixture();
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: wav }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'src' || key === 'kick' ? `take-${String(key)}` : undefined) }), configurable: true }); }));
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('duck'))).toBe(true);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.notes).toEqual([]);   // the key input is no longer left out
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('duck')?.left ?? 0)), { timeout: 8000 }).toBeGreaterThan(0.01);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
