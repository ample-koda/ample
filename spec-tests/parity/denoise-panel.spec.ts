import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The denoise panel shows the waveform of the input and a drag over a pause writes noise start, learn_from and learn.
const SONG = `composition "demo" { ample 1  tempo 92  master-seed 1 }
drums beats at 1 for 2 {
}
denoise clean {
  from beats.audio -> audio
}
chorus thick {
  from beats.audio -> audio
}
master master {
  from clean.audio -> audio
}
`;
freshServer({ song: SONG });

test('dragging over the waveform marks the noise of a denoise block', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'clean').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const wave = page.locator('#studio[open] canvas.denoise-wave');
  await expect(wave).toBeVisible();
  await expect(page.locator('#studio[open] .denoise-readout')).toBeVisible();
  // the input is decoded first; a drag before that does nothing, so drag again until it is written
  const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;
  for (let attempt = 0; attempt < 8 && !/noise start/.test(await docText()); attempt++) {
    await page.waitForTimeout(700);
    const box = (await wave.boundingBox())!;   // read again: the dialog can still be settling
    await page.mouse.move(box.x + box.width * 0.2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.5, box.y + box.height / 2, { steps: 5 });
    await page.mouse.up();
  }
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text, { timeout: 15000 }).toMatch(/noise start/);
});

test('denoise offers an enabled client-side preview for a built input', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'clean').getByRole('button', { name: 'Advanced', exact: true }).evaluate((button: HTMLElement) => button.click());
  const dialog = page.locator('#studio[open]');
  const preview = dialog.getByRole('button', { name: 'Client-side denoise preview' });
  await expect(preview).toBeEnabled();
  await expect(dialog.getByText(/client-side preview is approximate/)).toBeVisible();
  await preview.click();
  await expect(dialog.getByText('Client-side denoise preview · approximate.')).toBeVisible({ timeout: 15000 });
});

test('denoise "optimize" is shown apart from the sound controls and writes its choice', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'clean').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const dialog = page.locator('#studio[open]');
  await expect(dialog.getByText('Execution (never changes the sound):')).toBeVisible();
  const speed = dialog.getByRole('button', { name: 'Optimize: speed' });
  await expect(speed).toBeVisible();
  await speed.click();
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text).toMatch(/optimize speed/);
});

// Chorus has no worker-based render like denoise; it is one of the plain "live" effects (a persistent Web Audio graph updated in real time),
// so it reuses the generic "Live local preview" button and audibly demonstrates the effect instead of just playing the dry input.
test('chorus offers an enabled live local preview for a built input', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'thick').getByRole('button', { name: 'Advanced', exact: true }).evaluate((button: HTMLElement) => button.click());
  const dialog = page.locator('#studio[open]');
  const preview = dialog.getByRole('button', { name: 'Live local preview' });
  await expect(preview).toBeEnabled();
  await expect(dialog.getByText(/live preview is approximate/)).toBeVisible();
  await preview.click();
  await expect(dialog.getByText(/Live local preview · thick · approximate/)).toBeVisible({ timeout: 15000 });
});
