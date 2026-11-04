import { test, expect } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { api, freshServer } from '../../web/tests/support';

// An mp3 (or another compressed audio file) in the Project tab gets a waveform too: the server decodes it once with ffmpeg and sends the same summary as for a WAV.
const ffmpeg = spawnSync('ffmpeg', ['-version']);
freshServer({
  prepare: (_cli, folder) => {
    if (ffmpeg.status !== 0) return;
    spawnSync('ffmpeg', ['-v', 'error', '-y', '-f', 'lavfi', '-i', 'sine=frequency=440:duration=2', '-c:a', 'libmp3lame', path.join(folder, 'tone.mp3')]);
  },
});

test('the waveform summary of an mp3 comes back like the one of a WAV', async () => {
  test.skip(ffmpeg.status !== 0, 'ffmpeg is not installed');
  const peaks = (await api('/api/project/peaks?path=tone.mp3')) as { peaks: number[] };
  expect(peaks.peaks.length).toBeGreaterThan(100);
  expect(Math.max(...peaks.peaks)).toBeGreaterThan(0.05);
});
