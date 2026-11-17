import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServerPerTest, openApp } from '../../web/tests/support';
import { connect, startedChain, wavFixture } from './support/live-chain-fixture';

freshServerPerTest();

// A chain that is still preparing (its takes and impulses are fetched while the old chain keeps playing)
// is cancelled by Stop and by opening another project,
// and a switch that fails while preparing leaves the old chain playing.

// a new convolution block reads `ir file` with no file: no impulse response at all (AMP5350 on the server too), so the room gets one of the
// impulse-responses pack; where that pack is not installed the test cannot start a convolution chain and is skipped
async function withRoomImpulse(page) {
  await page.evaluate(() => import('/app/store.js').then((s) => s.edit([{ type: 'setParameter', block: 'room', name: 'ir', value: 'room' }])));
  const answer = await page.evaluate(async () => fetch('/api/effects/impulse', {
    method: 'POST', headers: { 'X-Ample-Token': (await import('/app/api.js')).getToken(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ path: (window as any).AMPLE_STATE.path, block: 'room', params: { ir: 'room' } }),
  }).then((r) => r.status));
  test.skip(answer !== 200, 'the impulse-responses pack is not installed here');
}

async function slowChain(page) {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'drums', 'src');
  await addTemplateBlock(page, 'eq', 'tone');
  await connect(page, 'src', 'tone');
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then(
    (s) => s.flat().links.some((l) => l.type === 'audio' && l.from === 'src' && l.to === 'tone'),
  ))).toBe(true);
  await page.route('**/api/takes/**/download*', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 1500));
    await route.fulfill({ status: 200, contentType: 'audio/wav', body: wavFixture() });
  });
  await page.evaluate(() => import('/app/store.js').then((s) => {
    const takes = new Proxy({}, { get: (_, key) => (key === 'src' ? 'take-src' : undefined) });
    Object.defineProperty(s.S.build, 'takes', { get: () => takes, set: () => {} });
  }));
}

for (const [name, cancel] of [
  ['Stop', "m.stopChain()"],
  ['opening another project', "document.dispatchEvent(new Event('ample:open'))"],
] as const) {
  test(`@audio ${name} while a chain is still preparing cancels that start`, async ({ page }) => {
    await slowChain(page);
    const started = await page.evaluate(async (code) => {
      const m = await import('/app/live-chain.js');
      const pending = m.startChain('tone');
      await new Promise((resolve) => setTimeout(resolve, 300));
      new Function('m', code)(m);
      return pending;
    }, cancel);
    expect(started).toBe(false);
    await page.waitForTimeout(2500);
    const active = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainActive()));
    expect(active, 'the cancelled start did not begin to play afterwards').toBe(false);
  });
}

test('@audio a switch that fails while preparing leaves the old chain playing', async ({ page }) => {
  await startedChain(page, [['convolution', 'room']], undefined, () => withRoomImpulse(page));
  let impulseRequests = 0;
  await page.route('**/api/effects/impulse', (route) => {
    impulseRequests++;
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'no impulse today' }) });
  });
  const switched = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.startChain('room')));
  expect(switched).toBe(false);
  expect(impulseRequests).toBeGreaterThan(0);
  expect(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainActive())), 'the old chain is still there').toBe(true);
  await page.waitForTimeout(600);
  const left = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('room')?.left ?? 0));
  expect(left, 'and still sounds').toBeGreaterThan(0.002);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});

test('@audio a song state that failed to start is not tried again on every change (no repeated fetch or notice)', async ({ page }) => {
  await startedChain(page, [['convolution', 'room']], undefined, () => withRoomImpulse(page));
  let impulseRequests = 0;
  await page.route('**/api/effects/impulse', (route) => {
    impulseRequests++;
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'no impulse today' }) });
  });
  // a new block in the chain (src -> mid -> room): the song state changes, the chain restarts, the restart fails. An edit in the page and not
  // addTemplateBlock, which reloads the page and so ends the chain this test watches; a block after room would not be part of the chain at all
  const inserted = await page.evaluate(async () => {
    const s = await import('/app/store.js');
    const link = s.flat().links.find((l) => l.from === 'src' && l.to === 'room');
    return s.edit([{ type: 'addBlock', blockType: 'eq', name: 'mid' }, { type: 'disconnect', to: 'room', input: link.inName },
      { type: 'connect', from: 'src', output: link.outName, to: 'mid', input: 'in' }, { type: 'connect', from: 'mid', output: 'out', to: 'room', input: link.inName }]);
  });
  expect(inserted).toBe(true);
  await expect.poll(() => impulseRequests).toBeGreaterThan(0);
  await page.waitForTimeout(800);
  const afterFailure = impulseRequests;
  await page.evaluate(() => import('/app/store.js').then((s) => { s.notify(); }));   // more edits of the same state
  await page.waitForTimeout(800);
  expect(impulseRequests, 'the failed state was not tried again').toBe(afterFailure);
  expect(await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainActive())), 'the old chain still plays').toBe(true);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
