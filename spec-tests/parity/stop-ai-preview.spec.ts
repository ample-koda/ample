import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// The audio of an AI proposal (or "hear the original") plays through its own audio element. Stop must also clear
// the "playing" state of the top bar: the audio element's own events do not, because stopping it reloads it.
test('Stop ends the AI preview audio and the Stop button goes back to rest', async ({ page }) => {
  await openApp(page);
  const wav = Buffer.alloc(44 + 44100 * 2 * 3);   // 3 s of silence, 22.05 kHz would do, 44.1 kHz mono 16 bit here
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVEfmt ', 8); wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22);
  wav.writeUInt32LE(44100, 24); wav.writeUInt32LE(88200, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(wav.length - 44, 40);
  await page.route('**/api/takes/**/download?format=wav', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: wav }));
  await page.evaluate(async () => {
    const { S } = await import('/app/store.js');
    S.build.takes = { ...(S.build.takes || {}), probe: 'sha256:probe' };
    const { hearOriginal } = await import('/app/ai.js');
    await hearOriginal('probe');
  });
  await expect(page.locator('#b-playing')).toContainText('the AI proposal');
  await expect(page.locator('#btn-stop')).toBeEnabled();
  await page.click('#btn-stop');
  await expect(page.locator('#b-playing')).toBeHidden();
  await expect(page.locator('#btn-stop')).toBeDisabled();
});
