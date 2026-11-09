import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// An m4a (AAC in an MP4 container) can be uploaded like an mp3: it is kept and a WAV is made next to it for the blocks.
freshServer();

test('an m4a upload is accepted, kept, and gets a WAV next to it', async ({ page }) => {
  const { execFileSync } = await import('node:child_process');
  const os = await import('node:os'), fs = await import('node:fs'), path = await import('node:path');
  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ample-m4a-')), 'voice.m4a');
  try { execFileSync(process.env.AMPLE_FFMPEG || 'ffmpeg', ['-y', '-loglevel', 'error', '-f', 'lavfi', '-i', 'anoisesrc=d=2:c=pink:a=0.3', '-ac', '2', '-ar', '44100', '-c:a', 'aac', file]); } catch { test.skip(true, 'ffmpeg with AAC is not installed'); }
  await openApp(page, { reset: false });
  const base64 = fs.readFileSync(file).toString('base64');
  const up = await page.evaluate(async (data) => {
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    const { api } = await import('/app/api.js');
    return api.uploadProject(new File([bytes], 'voice.m4a'), 'assets', 'refuse', () => {});
  }, base64);
  expect(up.path).toBe('assets/voice.m4a');
  expect(up.converted).toBe('assets/voice.wav');
  const tree = JSON.stringify(await api('/api/project/tree?path=assets&depth=0'));
  expect(tree).toContain('voice.m4a');
  expect(tree).toContain('voice.wav');
});
