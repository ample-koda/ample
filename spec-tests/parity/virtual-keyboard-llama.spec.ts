import { test, expect } from '@playwright/test';
import { freshServer, node, openApp } from '../../web/tests/support';

// The Delay Llama window has keys under its strip: a key sings the voice of the block, and a MIDI input plays them too.
const SONG = `composition "demo" { ample 1  tempo 120  master-seed 1 }
midi-clip line at 1 for 2 {
  bars 2
  notes 1:0:4:57:100 2:0:4:64:100
}
delay-llama llama {
  from line.notes -> notes
}
master master {
  from llama.audio -> audio
}
`;
freshServer({ song: SONG });

test('the llama window has keys that light up for a touch and for a MIDI input', async ({ page }) => {
  await page.addInitScript(() => {
    const input: { onmidimessage: ((m: { data: number[] }) => void) | null; name: string } = { onmidimessage: null, name: 'Virtual cable' };
    (window as unknown as { __fakeMidi: unknown }).__fakeMidi = { send: (data: number[]) => input.onmidimessage?.({ data }) };
    (navigator as unknown as { requestMIDIAccess: () => Promise<unknown> }).requestMIDIAccess = async () => ({ inputs: new Map([['fake', input]]), onstatechange: null });
  });
  await openApp(page, { reset: false });
  await node(page, 'llama').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const keys = page.locator('#studio[open] .llama-keys .vkb');
  await expect(keys).toBeVisible();
  const c4 = keys.locator('.vkb-key[data-pitch="60"]');
  await c4.scrollIntoViewIfNeeded();
  const box = (await c4.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height * 0.6);
  await page.mouse.down();
  await expect(c4).toHaveClass(/on/);
  await page.mouse.up();
  await expect(c4).not.toHaveClass(/on/);
  await page.locator('#studio[open] .llama-midi').click();
  await expect(page.locator('#studio[open] .llama-midi')).toHaveAttribute('aria-pressed', 'true');
  await page.evaluate(() => (window as any).__fakeMidi.send([0x90, 64, 100]));
  await expect(keys.locator('.vkb-key[data-pitch="64"]')).toHaveClass(/on/);
  await page.evaluate(() => (window as any).__fakeMidi.send([0x80, 64, 0]));
  await expect(keys.locator('.vkb-key[data-pitch="64"]')).not.toHaveClass(/on/);
});

test('the dock moves into the Advanced window while it is open (a modal dialog) and plays the llama with its knobs', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#btn-keys').click();
  const dock = page.locator('#keys-dock');
  await expect(dock).toBeVisible();
  await node(page, 'llama').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  // one keyboard at a time: the window leaves its own keys out while the dock is open
  await expect(page.locator('#studio[open] .llama-keys .vkb')).toBeHidden();
  // the dock is inside the open dialog now, so it can be used
  await expect(page.locator('#studio[open] #keys-dock')).toHaveCount(1);
  await expect(dock.locator('.keys-target')).toContainText('Delay Llama');
  await dock.getByRole('button', { name: 'Maximize the keyboard' }).click();
  await expect(dock.locator('.keys-knobs .studio-panel-control')).not.toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(page.locator('#studio[open]')).toHaveCount(0);
  // the window is closed: the dock is back in the page
  await expect(page.locator('body > #keys-dock')).toHaveCount(1);
});
