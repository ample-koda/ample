import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServerPerTest, node, openApp } from '../../web/tests/support';

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

test('@audio maximize and Esc while a live chain plays: the sound goes on and the first Esc only restores the window', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src');
  await addTemplateBlock(page, 'eq', 'tone');
  await addTemplateBlock(page, 'reverb', 'room');
  await connect(page, 'src', 'tone'); await connect(page, 'tone', 'room');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.flat().links.filter((l) => l.type === 'audio').length))).toBeGreaterThanOrEqual(2);
  const wav = wavFixture();
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: wav }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'src' ? 'take-src' : key === 'room' && (window as any).__roomBuilt ? 'take-room' : undefined) }), set: () => {} }); }));
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('room'))).toBe(true);
  const playing = () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing);
  await expect.poll(playing).toBe(true);
  await node(page, 'tone').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const w = page.locator('#studio[open]');
  await expect(w).toBeVisible();
  await w.locator('.mls-maximize').click();
  await expect(w).toHaveAttribute('data-window', 'max');
  expect(await playing()).toBe(true);
  await page.keyboard.press('Escape');
  await expect(w).not.toHaveAttribute('data-window', 'max');
  await expect(w).toBeVisible();
  await page.waitForTimeout(500);
  expect(await playing()).toBe(true);
});

test('the light theme keeps the title bar of the window readable', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(() => import('/app/ui.js').then((u) => u.setTheme('light')));
  await addTemplateBlock(page, 'compressor', 'm-comp');
  await node(page, 'm-comp').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const w = page.locator('#studio[open]');
  await expect(w).toBeVisible();
  await expect(w.getByRole('switch', { name: 'Effect enabled' })).toBeVisible();
  const lum = (css: string) => { const m = (css.match(/[\d.]+/g) || ['0', '0', '0']).map(Number).slice(0, 3).map((v) => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; }); return 0.2126 * m[0] + 0.7152 * m[1] + 0.0722 * m[2]; };
  // the title bar keeps its dark metal look in the light theme, so its title, status text and the power icon must stay light
  const colours = await w.locator('.studio-dialog-header').evaluate((h) => ['strong', '.studio-state-text', '.studio-power'].map((s) => getComputedStyle(h.querySelector(s)!).color));
  for (const colour of colours) expect(lum(colour)).toBeGreaterThan(0.4);
});
