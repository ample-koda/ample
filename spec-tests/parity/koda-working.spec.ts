import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// While Koda works: the bubble can be clicked away and comes back, extra input steers, "stop and listen" and a hard stop end the turn but leave a way to go on; fields take the caret at once on a computer.
test.beforeEach(async ({ page }) => { await openApp(page); });

const state = (page) => page.evaluate(async () => (await import('/app/koda.js')).kodaState());

async function startWorking(page) {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__runs = [];
    (window as any).__steered = [];
    (window as any).__interrupts = 0;
    setBackend({
      explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }),
      steer: async (text) => { (window as any).__steered.push(text); },
      interrupt: async () => { (window as any).__interrupts++; },
      run: (_subject, text, opts) => { (window as any).__runs.push(text); return new Promise((_, reject) => opts.signal.addEventListener('abort', () => reject(new Error('stopped')))); },
    });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  const field = page.locator('#koda-layer .bub.k-choice input');
  await expect(field).toBeFocused();   // the caret is there at once
  await field.fill('put a reverb after it');
  await page.keyboard.press('Enter');
  await expect(page.locator('#koda-layer .bub.k-thought')).toBeVisible();
}

test('the working bubble can be clicked away and comes back with a click on Koda, and extra input is sent to the work', async ({ page }) => {
  await startWorking(page);
  await page.locator('#koda-layer .bub.k-thought .tctl input').fill('make it short');
  await page.keyboard.press('Enter');
  await expect.poll(() => page.evaluate(() => (window as any).__steered)).toEqual(['make it short']);
  await page.locator('#koda-layer .bub.k-thought .x').click();
  await expect(page.locator('#koda-layer .bub')).toHaveCount(0);
  await page.keyboard.press('Control+k');
  await expect(page.locator('#koda-layer .bub.k-thought')).toBeVisible();   // what Koda is doing, not the menu
  await expect(page.locator('#koda-layer .bub.k-menu')).toHaveCount(0);
});

test('stop and listen ends the turn and opens a field at once; what is typed goes on with what was being done', async ({ page }) => {
  await startWorking(page);
  await page.locator('#koda-layer .bub.k-thought .tctl').getByRole('button', { name: 'Stop and listen' }).click();
  expect(await page.evaluate(() => (window as any).__interrupts)).toBe(1);   // the turn is interrupted, the conversation is not cancelled
  const bubble = page.locator('#koda-layer .bub.k-answer');
  await expect(bubble).toBeVisible();
  const field = bubble.locator('.other input');
  await expect(field).toBeFocused();
  await field.fill('use a plate reverb instead');
  await page.keyboard.press('Enter');
  await expect.poll(() => page.evaluate(() => (window as any).__runs.length)).toBe(2);
  const second = await page.evaluate(() => (window as any).__runs[1]);
  expect(second).toContain('put a reverb after it');
  expect(second).toContain('use a plate reverb instead');
});

test('a hard stop leaves thanks, continue and ask (the field stays closed until Ask)', async ({ page }) => {
  await startWorking(page);
  await page.locator('#koda-layer .bub.k-thought .tctl').getByRole('button', { name: 'Stop', exact: true }).click();
  expect(await page.evaluate(() => (window as any).__interrupts)).toBe(1);
  const bubble = page.locator('#koda-layer .bub.k-answer');
  await expect(bubble).toBeVisible();
  await expect(bubble.locator('.other')).toBeHidden();
  await expect(bubble.getByRole('button', { name: 'Continue' })).toBeVisible();
  await bubble.getByRole('button', { name: 'Ask a question' }).click();
  await expect(bubble.locator('.other input')).toBeFocused();
  await bubble.getByRole('button', { name: 'Continue' }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__runs.length)).toBe(2);
});
