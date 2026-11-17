import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// GUI flow 4, master: loudness (short-term and integrated LUFS) and a peak readout with hold next to the master fader, which keeps writing the limiter ceiling.
freshServer();

function wav(seconds: number) {
  const rate = 44_100, frames = rate * seconds, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24);
  bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let frame = 0; frame < frames; frame++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * frame / rate) * 16000), 44 + frame * 2);
  return bytes;
}

test('the master strip has a loudness readout and a peak readout; they follow the song that plays and Reset clears them', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#btabs button[data-t="mixer"]').click();
  const master = page.locator('#b-mixer .mixstrip[data-block="master"]');
  await expect(master.locator('.mixlufs-s')).toHaveText('—');
  await expect(master.locator('.mixlufs')).toContainText('target -14 LUFS');
  const base64 = wav(8).toString('base64');
  await page.evaluate(async (data) => {
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    const player = await import('/app/player.js');
    await player.playTransient(new Blob([bytes], { type: 'audio/wav' }));
  }, base64);
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing)).toBe(true);
  await expect.poll(async () => Number(await master.locator('.mixlufs-s').textContent()), { timeout: 15000 }).toBeLessThan(-5);
  expect(Number(await master.locator('.mixlufs-s').textContent())).toBeGreaterThan(-30);
  expect(Number(await master.locator('.mixpeakval').textContent())).toBeLessThan(0);
  expect(Number(await master.locator('.mixpeakval').textContent())).toBeGreaterThan(-30);
  await master.getByRole('button', { name: 'Reset loudness and peak' }).click();
  await expect(master.locator('.mixlufs-i')).toHaveText(/—|-/);
});

test('the master fader still writes the limiter ceiling', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#btabs button[data-t="mixer"]').click();
  const fader = page.locator('#b-mixer .mixstrip[data-block="master"] .mixstripfader');
  await expect(fader).toHaveAttribute('aria-label', 'master limiter ceiling');
  await fader.evaluate((i: HTMLInputElement) => { i.value = '-4'; i.dispatchEvent(new Event('change', { bubbles: true })); });
  await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => String(s.flat().nodes.find((n) => n.id === 'master')!.params.limiter)))).toContain('-4');
});
