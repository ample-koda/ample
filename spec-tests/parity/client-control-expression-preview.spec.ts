import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

// A parameter that is an expression over controls (cutoff 400 + 3000 * sweep.level) is made in the client preview as the server makes it (ControlExpression, points every
// millisecond). The level over time of the preview output is compared with the take the server builds afterwards.

function sourceWav() {
  const frames = 44100 * 4, data = Buffer.alloc(frames * 2), wav = Buffer.alloc(44 + data.length);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVE', 8); wav.write('fmt ', 12);
  wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(44100, 24);
  wav.writeUInt32LE(88200, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(data.length, 40);
  for (let frame = 0; frame < frames; frame++) data.writeInt16LE(Math.round(Math.sin(frame * Math.PI / 32) * 8000 * (frame % 22050 < 4000 ? 1 : 0.2)), frame * 2);
  data.copy(wav, 44);
  return wav;
}

freshServer({ prepare: (_cli, folder) => fs.writeFileSync(path.join(folder, 'source.wav'), sourceWav()) });

const head = `composition "Control expression" { ample 1  tempo 120  master-seed 17 }
import-audio source at 1 for 2 {
  file source.wav
}
`;
const tail = `master master {
  from echo.audio -> audio
}
`;

async function previewThenBuild(page, song: string, block: string) {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), song);
  await page.evaluate((name) => { (window as any).__tapped = null; (window as any).__previewTap = (id, _out, audio) => { if (id === name && audio?.channels) (window as any).__tapped = { rate: audio.sampleRate, left: Array.from(audio.channels[0]) }; }; }, block);
  const builds: unknown[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON()); });
  await page.click('#btn-play-options');
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 30000 }).toBe(true);
  await page.click('#btn-stop');
  expect(builds, 'the preview made the block itself').toHaveLength(0);
  const local = await page.evaluate(() => (window as any).__tapped);
  expect(local, 'the preview tap saw the block').not.toBeNull();
  // the build of the server, and its take of the same block (16-bit samples)
  const server = await page.evaluate(async (name) => {
    const s = await import('/app/store.js'), api = await import('/app/api.js');
    await s.build();
    const take = s.S.build.takes[name];
    const bytes = new Uint8Array(await (await fetch('/api/takes/' + encodeURIComponent(take) + '/download?format=wav', { headers: { 'X-Ample-Token': api.getToken() } })).arrayBuffer());
    const view = new DataView(bytes.buffer), channels = view.getUint16(22, true);
    let at = 12; while (at < bytes.length - 8 && String.fromCharCode(...bytes.slice(at, at + 4)) !== 'data') at += 8 + view.getUint32(at + 4, true);
    const left: number[] = []; for (let i = at + 8; i + 1 < bytes.length; i += 2 * channels) left.push(view.getInt16(i, true));
    return left;
  }, block);
  return { local: local.left.map((v: number) => Math.round(v * 32768)), rate: local.rate, server };
}

/** The largest difference in dB between the levels (RMS per quarter second) of the preview and of the build: the preview runs at its own sample rate, so the
 *  samples themselves are not comparable, the level over time is. Quiet windows (under -50 dBFS on both) are left out. */
function levelDifference(local: number[], localRate: number, server: number[], serverRate = 44100) {
  const rms = (a: number[], from: number, to: number) => { let s = 0; for (let i = from; i < to; i++) s += a[i] * a[i]; return Math.sqrt(s / Math.max(1, to - from)) / 32768; };
  const db = (v: number) => 20 * Math.log10(Math.max(v, 1e-9));
  let worst = 0, windows = 0;
  const seconds = Math.min(local.length / localRate, server.length / serverRate);
  for (let t0 = 0; t0 + 0.25 <= seconds; t0 += 0.25) {
    const a = db(rms(local, Math.round(t0 * localRate), Math.round((t0 + 0.25) * localRate))), b = db(rms(server, Math.round(t0 * serverRate), Math.round((t0 + 0.25) * serverRate)));
    if (a < -50 && b < -50) continue;
    windows++; worst = Math.max(worst, Math.abs(a - b));
  }
  return { worst, windows };
}

test('a filter whose cutoff is an expression over a curve sounds as the build', async ({ page }) => {
  const song = head + `curve sweep at 1 for 2 {
  value auto(1:0, 3:1000)
}
filter echo {
  type lowpass
  from source.audio -> audio
  cutoff 200 + 3000 * sweep.level
}
` + tail;
  const { local, rate, server } = await previewThenBuild(page, song, 'echo');
  const { worst, windows } = levelDifference(local, rate, server);
  console.log('LEVEL', worst.toFixed(2), windows);
  expect(windows).toBeGreaterThan(8);
  expect(worst, 'largest level difference in dB against the build').toBeLessThanOrEqual(1);
});

test('a delay whose mix is an expression over follow and map sounds as the build', async ({ page }) => {
  const song = head + `follow loud {
  attack 20 ms
  release 200 ms
  baseline off
  from source.audio -> audio
}
delay echo {
  time 250 ms
  feedback 40 %
  mix 100 * loud.level
  from source.audio -> audio
}
` + tail;
  const { local, rate, server } = await previewThenBuild(page, song, 'echo');
  const { worst, windows } = levelDifference(local, rate, server);
  console.log('LEVEL', worst.toFixed(2), windows);
  expect(windows).toBeGreaterThan(8);
  expect(worst, 'largest level difference in dB against the build').toBeLessThanOrEqual(1);
});
