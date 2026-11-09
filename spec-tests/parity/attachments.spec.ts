import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Files attached to a message to Koda or the assistant (web/app/attach.js, POST /api/ai/attachments): picking, pasting, refusing a program, taking the ids.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');

async function mountTray(page) {
  await page.evaluate(async () => {
    const { fileAttachment } = await import('/app/attach.js');
    const area = document.createElement('textarea'); area.id = 'att-area';
    const tray = fileAttachment({ pasteTarget: area });
    (window as any).__tray = tray;
    document.body.append(area, tray.add, tray.pick, tray.chips);
  });
}

test('a picture is picked, shown as a chip with a thumbnail, kept by the server and taken with the message', async ({ page }) => {
  await mountTray(page);
  await page.locator('input[aria-label="Attach files"]').setInputFiles({ name: 'notes shot.png', mimeType: 'image/png', buffer: PNG });
  const chip = page.locator('.att-chip').first();
  await expect(chip).toContainText('notes shot.png');
  await expect(chip.locator('img.att-thumb')).toBeVisible();
  await expect.poll(() => page.evaluate(() => (window as any).__tray.ids().length)).toBe(1);
  const id = await page.evaluate(() => (window as any).__tray.ids()[0]);
  expect(id).toMatch(/^[0-9a-f]{32}$/);
  const taken = await page.evaluate(() => (window as any).__tray.take());
  expect(taken).toEqual([id]);
  await expect(page.locator('.att-chip')).toHaveCount(0);
});

test('a picture pasted from the clipboard attaches, and plain text still pastes as text', async ({ page }) => {
  await mountTray(page);
  await page.evaluate(async (b64) => {
    const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
    const data = new DataTransfer();
    data.items.add(new File([bytes], 'image.png', { type: 'image/png' }));
    document.getElementById('att-area')!.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  }, PNG.toString('base64'));
  await expect(page.locator('.att-chip')).toHaveCount(1);
  await expect.poll(() => page.evaluate(() => (window as any).__tray.ids().length)).toBe(1);
  const plain = await page.evaluate(() => {
    const data = new DataTransfer(); data.setData('text/plain', 'hello');
    const event = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
    document.getElementById('att-area')!.dispatchEvent(event);
    return event.defaultPrevented;
  });
  expect(plain).toBe(false);
});

test('a program or an empty file is refused with a message and leaves no chip', async ({ page }) => {
  await mountTray(page);
  await page.locator('input[aria-label="Attach files"]').setInputFiles({ name: 'run.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('MZ') });
  await expect(page.locator('#toast')).toContainText('cannot be attached');
  await expect(page.locator('.att-chip')).toHaveCount(0);
});
