import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// X03 (plan/mix-and-record/X03-limiter-pan-width.md): the master block's own panel shows its measured peak and
// integrated loudness with the studio meter widget (web/app/studio/meter.js), built earlier but never wired in.
freshServer({
  song: 'composition "band" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords {\n  chords Am F\n  bars 2\n}\ndrums kit at 1 for 2 {\n  kit samples/kit\n  pattern beat-A\n}\nmixer mix {\n  from kit.audio -> audio1\n}\nmaster master {\n  from mix.audio -> audio\n}\n',
  prepare: (_cli, folder) => {
    const wav = (samples: number[]) => {
      const b = Buffer.alloc(44 + samples.length * 2);
      b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
      b.writeInt32LE(44100, 24); b.writeInt32LE(44100 * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
      samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
      return b;
    };
    const tone = (n: number, level = 6000) => wav([...Array(n)].map((_, i) => Math.round(level * Math.exp(-i / 1000) * Math.sin(i / 9))));
    for (const v of ['kick', 'snare', 'hat']) { const p = path.join(folder, 'samples', 'kit', `${v}.wav`); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, tone(2000)); }
  },
});

test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

test('the master panel measures the built song and shows a peak and loudness readout', async ({ page }) => {
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet', intent: 'bake' } });
  await node(page, 'master').locator('.nh .t').click();
  const text = page.locator('.master-meter-text');
  await expect(text).toHaveText(/Peak -?\d+\.\d dBFS · RMS -?\d+\.\d dBFS · Loudness (-?\d+\.\d LUFS|n\/a)/);
  await expect(page.locator('.master-meter-bars .studio-meter')).toHaveCount(2);
});
