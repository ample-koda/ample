import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

const song = `composition "Local synth preview" { ample 1  tempo 120  master-seed 17 }

midi-clip phrase {
  chords C Am F G
  bars 4
}

transpose octave {
  semitones 12
  from phrase.notes -> notes
}

synth instrument {
  wave triangle
  gain -12 dB
  from octave.notes -> notes
}

bass bassline {
  set analog-808
  pattern bass-A
  humanize 0
  from octave.notes -> notes
}

sampler sample {
  file recordings/sampler-test.wav
  root 60
  mode oneshot
  from octave.notes -> notes
}

piano felt {
  set felt-piano
  humanize 0
  from octave.notes -> notes
}

mixer mix {
  from instrument.audio -> audio1
  from bassline.audio -> audio2
  from sample.audio -> audio3
  from felt.audio -> audio4
}

master master {
  from mix.audio -> audio
}
`;

function sampleWav() {
  const frames = 8000, data = Buffer.alloc(frames * 2), wav = Buffer.alloc(44 + data.length);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVE', 8); wav.write('fmt ', 12);
  wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(8000, 24);
  wav.writeUInt32LE(16000, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(data.length, 40);
  for (let frame = 0; frame < frames; frame++) data.writeInt16LE(Math.round(Math.sin(frame * 2 * Math.PI * 220 / 8000) * 12000), frame * 2);
  data.copy(wav, 44);
  return wav;
}

freshServer({ song, prepare: (_cli, folder) => { fs.mkdirSync(path.join(folder, 'recordings'), { recursive: true }); fs.writeFileSync(path.join(folder, 'recordings', 'sampler-test.wav'), sampleWav()); } });

test('simple midi-clip notes drive supported instruments locally without an audio build', async ({ page }) => {
  await openApp(page, { reset: false });
  await expect.poll(() => page.evaluate(() => (window as any).AMPLE_STATE.doc.diagnostics.filter((d: any) => d.severity === 'error').length)).toBe(0);
  const builds: unknown[] = [], clips: unknown[] = [], samples: string[] = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/build') && request.method() === 'POST') builds.push(request.postDataJSON());
    if (request.url().includes('/api/midi/clip-notes') && request.method() === 'POST') clips.push(request.postDataJSON());
    if (request.url().includes('/api/project/audio?')) samples.push(request.url());
  });
  await page.click('#btn-play-options');
  await expect(page.getByRole('menuitem', { name: /Play preview/ })).toBeEnabled();
  await page.getByRole('menuitem', { name: /Play preview/ }).click();
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_PLAYER.state().playing), { timeout: 20000 }).toBe(true);
  expect(clips).toHaveLength(1);
  expect(samples).toHaveLength(1);
  expect(builds).toHaveLength(0);
  await expect(page.locator('#toast')).toContainText('Local MIDI transforms: octave');
  await page.click('#btn-stop');
});
