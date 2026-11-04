import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

function wav(samples: number[], rate = 44100): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}

const put = (folder: string, rel: string, data: Buffer) => { const p = path.join(folder, rel); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, data); };

freshServer({
  song: 'composition "band" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords {\n  chords Am F\n  bars 2\n}\ndrums kit at 1 for 2 {\n  kit samples/kit\n  pattern kick:x.......x....... snare:....X.......X...\n}\nguitar gtr at 1 for 2 {\n  set samples/gtr\n  strum D...D.u.\n  from chords.notes -> notes\n}\nmixer mix {\n  from kit.audio -> audio1\n  from gtr.audio -> audio2\n}\nmaster master {\n  from mix.audio -> audio\n}\n',
  prepare: (_cli, folder) => {
    put(folder, 'samples/kit/kick.wav', wav([...Array(2000)].map((_, i) => Math.round(20000 * Math.exp(-i / 300) * Math.sin(i / 8)))));
    put(folder, 'samples/kit/snare.wav', wav([...Array(2000)].map((_, i) => Math.round(12000 * Math.exp(-i / 200) * (((i * 7919) % 200) / 100 - 1)))));
    for (const n of ['e2', 'a2', 'c3', 'eb3', 'gb3', 'a3', 'c4']) put(folder, `samples/gtr/${n}.wav`, wav([...Array(8000)].map((_, i) => Math.round(6000 * Math.exp(-i / 3000) * Math.sin(i / 11)))));
  },
});

const statuses = (page) => page.evaluate(() => Object.fromEntries(Object.entries((window as any).AMPLE_STATE.build.status).map(([k, v]: [string, any]) => [k, v.status])));

async function build(page) {
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
}

test('the guitar is in the palette and the kit and the guitar build from the samples of the project', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect(page.locator('#lib').getByText('Guitar (samples)')).toBeVisible();
  await build(page);
  const s = await statuses(page);
  expect(s.kit).toBe('rendered');
  expect(s.gtr).toBe('rendered');
  expect(s.master).toBe('rendered');
  await expect(node(page, 'gtr').locator('.player')).toBeVisible();
  await expect(node(page, 'kit').locator('.player')).toBeVisible();
});

test('changing the pattern in the source rebuilds only the drums and what follows, and the strum only the guitar', async ({ page }) => {
  await openApp(page, { reset: false });
  await build(page);
  let doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'setParameter', block: 'kit', name: 'pattern', value: 'kick:x...x...x...x... snare:....X.......X...' } } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await build(page);
  let s = await statuses(page);
  expect(s.kit).toBe('rendered');
  expect(s.gtr).toBe('cached');
  expect(s.mix).toBe('rendered');

  doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'setParameter', block: 'gtr', name: 'strum', value: 'D.u.D.u.' } } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await build(page);
  s = await statuses(page);
  expect(s.gtr).toBe('rendered');
  expect(s.kit).toBe('cached');
});

test('a pattern with a voice that has no sample is an error before the build instead of playing silence', async ({ page }) => {
  await openApp(page, { reset: false });
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'setParameter', block: 'kit', name: 'pattern', value: 'tom:x...' } } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  // the mistake is found before a build (AMP2088), so Build is off and the song says why instead of playing silence
  await expect(page.locator('#btn-build')).toBeDisabled();
  const shown = await api('/api/doc?path=song.amplet');
  expect(shown.diagnostics.map((d: any) => d.code)).toContain('AMP2088');
});
