import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// An answer that proposes something ("I would split this group…") has a Do that button: it asks Koda to do what it just suggested, with its own answer as the context.
test('an answer from the model has a Do that button that sends the suggestion back as a task', async ({ page }) => {
  await openApp(page);
  await page.locator('#btn-koda').click();
  await expect(page.locator('#koda-layer .bub')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__runs = [];
    setBackend({
      explain: async () => ({ kind: 'answer', title: 'More focused groups', text: 'I would split the crowded group by role.', more: 'Radio, Dream and Robot get their own group.', action: 'Split the crowded group into one group per role.' }),
      run: async (subject, text) => { (window as any).__runs.push({ said: subject.said, text }); return { kind: 'answer', title: 'x', text: 'y' }; },
    });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'What does this do?' }).click();
  const bubble = page.locator('#koda-layer .bub.k-answer');
  await bubble.getByRole('button', { name: 'Ask a question' }).click();
  await bubble.getByPlaceholder('Ask something about this…').fill('how would you tidy this up?');
  await page.keyboard.press('Enter');
  await expect(bubble).toContainText('More focused groups', { timeout: 8000 });
  await expect(bubble.getByRole('button', { name: 'Do that' })).toHaveAttribute('title', 'Split the crowded group into one group per role.');   // the tooltip says what it would do
  await bubble.getByRole('button', { name: 'Do that' }).click();
  await expect.poll(() => page.evaluate(() => (window as any).__runs.length)).toBe(1);
  const run = await page.evaluate(() => (window as any).__runs[0]);
  expect(run.text).toBe('Do what you just suggested in your answer. Split the crowded group into one group per role.');
  expect(run.said).toContain('I would split the crowded group by role.');
  expect(run.said).toContain('Radio, Dream and Robot');
});

test('the bubble of Koda at work is wide by default', async ({ page }) => {
  await openApp(page);
  await page.locator('#btn-koda').click();
  await expect(page.locator('#koda-layer .bub')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    setBackend({ explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }), run: () => new Promise(() => {}) });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('put a reverb after it');
  await page.keyboard.press('Enter');
  const thought = page.locator('#koda-layer .bub.k-thought');
  await expect(thought).toBeVisible();
  await expect.poll(async () => (await thought.boundingBox())!.width).toBeGreaterThanOrEqual(440);   // after the pop-in
});

// A pure explanation has nothing to do: the model gives no action and the answer has no Do that button.
test('an answer that only explains has no Do that button', async ({ page }) => {
  await openApp(page);
  await page.locator('#btn-koda').click();
  await expect(page.locator('#koda-layer .bub')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    setBackend({ explain: async () => ({ kind: 'answer', title: 'Four gestures', text: 'Four scratches at 1.5, 4.8, 6 and 14 seconds.', more: '' }), run: async () => ({ kind: 'answer', title: 'x', text: 'y' }) });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'What does this do?' }).click();
  const bubble = page.locator('#koda-layer .bub.k-answer');
  await bubble.getByRole('button', { name: 'Ask a question' }).click();
  await bubble.getByPlaceholder('Ask something about this…').fill('what do these gestures do?');
  await page.keyboard.press('Enter');
  await expect(bubble).toContainText('Four gestures', { timeout: 8000 });
  await expect(bubble.getByRole('button', { name: 'Thanks' })).toBeVisible();
  await expect(bubble.getByRole('button', { name: 'Do that' })).toHaveCount(0);
});
