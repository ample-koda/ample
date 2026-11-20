import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

// A pitch whose semitones follow a curve (auto(...), lfo, or a curve control connected to it) is made in the client preview as the server makes it. The pitch of the
// preview output is compared per quarter second (zero crossings of a tone) with the take the server builds afterwards.

function sourceWav() {
  const frames = 44100 * 4, data = Buffer.alloc(frames * 2), wav = Buffer.alloc(44 + data.length);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVE', 8); wav.write('fmt ', 12);
  wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(44100, 24);
  wav.writeUInt32LE(88200, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(data.length, 40);
  for (let frame = 0; frame < frames; frame++) data.writeInt16LE(Math.round(Math.sin(frame * Math.PI / 32) * 6000), frame * 2);
  data.copy(wav, 44);
  return wav;
}

freshServer({ prepare: (_cli, folder) => fs.writeFileSync(path.join(folder, 'source.wav'), sourceWav()) });

const head = `composition "Pitch bend" { ample 1  tempo 120  master-seed 17 }
import-audio source at 1 for 2 {
  file source.wav
}
`;
const tail = `master master {
  from bend.audio -> audio
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

/** The frequency of a tone per quarter second from its zero crossings (rising ones), in Hz. */
function frequencies(samples: number[], rate: number) {
  const out: number[] = [];
  for (let t0 = 0; t0 + 0.25 <= samples.length / rate; t0 += 0.25) {
    let crossings = 0;
    const from = Math.round(t0 * rate), to = Math.round((t0 + 0.25) * rate);
    for (let i = from + 1; i < to; i++) if (samples[i - 1] < 0 && samples[i] >= 0) crossings++;
    out.push(crossings / 0.25);
  }
  return out;
}

async function compare(page, song: string) {
  const { local, rate, server } = await previewThenBuild(page, song, 'bend');
  const a = frequencies(local, rate), b = frequencies(server, 44100);
  const windows = Math.min(a.length, b.length);
  let worst = 0;
  for (let i = 0; i < windows; i++) worst = Math.max(worst, Math.abs(a[i] / b[i] - 1));
  console.log('PITCH', worst.toFixed(3), windows, a.slice(0, 16).map(Math.round).join(' '), '|', b.slice(0, 16).map(Math.round).join(' '));
  return { worst, windows, first: a[0], last: a[windows - 1] };
}

test('a pitch that follows auto(...) bends as the build', async ({ page }) => {
  const song = head + `pitch bend {
  semitones auto(1:0, 3:12)
  from source.audio -> audio
}
` + tail;
  const { worst, windows, first, last } = await compare(page, song);
  expect(windows).toBeGreaterThan(8);
  expect(last / first, 'the tone went up an octave').toBeGreaterThan(1.7);
  expect(worst, 'largest relative difference of the pitch against the build').toBeLessThanOrEqual(0.03);
});

test('a pitch that follows a curve control bends as the build', async ({ page }) => {
  const song = head + `curve sweep at 1 for 2 {
  value auto(1:500, 3:1000)
}
pitch bend {
  from source.audio -> audio
  from sweep.level -> semitones
}
` + tail;
  const { worst, windows, first, last } = await compare(page, song);
  expect(windows).toBeGreaterThan(8);
  expect(last / first, 'the tone went up an octave').toBeGreaterThan(1.7);
  expect(worst, 'largest relative difference of the pitch against the build').toBeLessThanOrEqual(0.03);
});
