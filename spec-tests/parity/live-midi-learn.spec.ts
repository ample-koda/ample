import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();

// Local preview 6.1: a stubbed Web MIDI input (navigator.requestMIDIAccess): learn a knob, move the controller, the knob and the song follow, one undo step per movement.
test('@audio a knob learns a controller, follows it, writes once, and Shift + right-click clears it', async ({ page }) => {
  await page.addInitScript(() => {
    const input: any = { id: 'fake', name: 'Fake controller', onmidimessage: null };
    (window as any).__midi = (bytes: number[]) => input.onmidimessage?.({ data: new Uint8Array(bytes) });
    (navigator as any).requestMIDIAccess = async () => ({ inputs: new Map([['fake', input]]), onstatechange: null });
  });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'eq', 'tone');
  await page.locator('.node[data-id="tone"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  const knob = dialog.locator('svg[role="slider"][aria-label="High"]').first();
  await expect(knob).toBeVisible();
  const text = async () => knob.getAttribute('aria-valuetext');
  const before = await text();
  await knob.click({ button: 'right', modifiers: ['Shift'] });
  await expect(knob).toHaveClass(/midi-learning/);
  await page.evaluate(() => (window as any).__midi([0xb0, 21, 10]));
  await expect(knob).toHaveClass(/midi-learned/);
  await expect(knob).not.toHaveClass(/midi-learning/);
  // the controller moves the knob; a burst closes into one write
  const versionBefore = await page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version));
  for (const value of [20, 60, 100, 127]) await page.evaluate((v) => (window as any).__midi([0xb0, 21, v]), value);
  await expect.poll(text).not.toBe(before);
  expect(Number(await knob.getAttribute('aria-valuenow'))).toBeCloseTo(Number(await knob.getAttribute('aria-valuemax')), 0);   // 127 is the top
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version)), { timeout: 5000 }).toBeGreaterThan(versionBefore);
  const written = await page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version));
  await page.waitForTimeout(800);
  expect(await page.evaluate(() => import('/app/store.js').then((s) => s.S.doc.version))).toBe(written);   // one write for the whole burst
  // an unbound controller does nothing; clearing releases the knob
  const value = await knob.getAttribute('aria-valuenow');
  await page.evaluate(() => (window as any).__midi([0xb0, 22, 0]));
  expect(await knob.getAttribute('aria-valuenow')).toBe(value);
  await knob.click({ button: 'right', modifiers: ['Shift'] });
  await expect(knob).not.toHaveClass(/midi-learned/);
  await page.evaluate(() => (window as any).__midi([0xb0, 21, 0]));
  await page.waitForTimeout(200);
  expect(await knob.getAttribute('aria-valuenow')).toBe(value);
});
