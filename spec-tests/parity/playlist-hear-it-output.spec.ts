import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Playlist "Hear it" from an idle transport: it plays through the shared output (listening volume), the output level is
// readable for the Mixer meter, and the main Stop ends it.
freshServer();
const wav = () => {
  const rate = 44100, n = rate * 3, buf = Buffer.alloc(44 + n * 2);
  buf.write('RIFF', 0); buf.writeUInt32LE(36 + n * 2, 4); buf.write('WAVEfmt ', 8); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22);
  buf.writeUInt32LE(rate, 24); buf.writeUInt32LE(rate * 2, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34); buf.write('data', 36); buf.writeUInt32LE(n * 2, 40);
  for (let i = 0; i < n; i++) buf.writeInt16LE(Math.round(Math.sin(i * 2 * Math.PI * 220 / rate) * 0.6 * 32767), 44 + i * 2);
  return buf;
};
test('Hear it from idle: volume, output meter and transport Stop', async ({ page }) => {
  await page.route('**/api/takes/t1', (r) => r.fulfill({ json: { audioDataId: 'd1' } }));
  await page.route('**/api/data/d1', (r) => r.fulfill({ body: wav(), contentType: 'audio/wav' }));
  await openApp(page, { reset: false });
  const result = await page.evaluate(async () => {
    const { S } = await import('/app/store.js');
    const { playMix } = await import('/app/playlist-preview.js');
    const { playerState, stop } = await import('/app/player.js');
    const { outputLevels, setPlaybackVolume } = await import('/app/playback-volume.js');
    const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));
    const peak = async () => { let m = 0; for (let i = 0; i < 12; i++) { await wait(25); m = Math.max(m, outputLevels().left); } return m; };
    (S as any).build = { ...(S as any).build, takes: { b1: 't1' } };
    const lines = [{ id: 'a', name: 'a', chain: ['b1'], sources: [] }];
    stop(); setPlaybackVolume(1);
    const idle = playerState().playing;
    const ok = await playMix(lines as any, { firstBar: 1, bars: 1 } as any, 2);
    const playing = playerState().playing;
    const full = await peak();
    setPlaybackVolume(0.25);
    const quiet = await peak();
    setPlaybackVolume(1);
    stop();
    const after = playerState().playing;
    await wait(150);
    return { idle, ok, playing, full, quiet, after, silent: outputLevels().left };
  });
  expect(result.idle).toBe(false); expect(result.ok).toBe(true);
  expect(result.playing).toBe(true);
  expect(result.full).toBeGreaterThan(0.1);
  expect(result.quiet).toBeLessThan(result.full * 0.6);
  expect(result.after).toBe(false);
  expect(result.silent).toBeLessThan(0.01);
});
