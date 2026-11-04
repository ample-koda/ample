import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// In the Project tab, right-click on an empty spot of a folder: Add has a recording and a hum to MIDI that start here, and the hum is saved as a .mid in that folder.
freshServer();

test('the Add menu of a folder can record here and hum to MIDI here', async ({ page }) => {
  await openApp(page);
  await page.click('#btabs button[data-t="project"]');
  const content = page.locator('#project-panel-content');
  await expect(content).toBeVisible();
  await content.dispatchEvent('contextmenu', { clientX: 400, clientY: 300, bubbles: true, cancelable: true });
  await page.locator('#menu').getByText('Add', { exact: false }).first().hover();
  await expect(page.locator('#menu').getByText('Record audio here...')).toBeVisible();
  await expect(page.locator('#menu').getByText('Hum to MIDI here...')).toBeVisible();
});

test('the route that saves a hum as MIDI writes a unique .mid in the folder', async () => {
  const notes = [{ start: 0, duration: 0.5, pitch: 60, strength: 1 }, { start: 0.5, duration: 0.5, pitch: 64, strength: 0.8 }];
  const first = (await api('/api/hum/save-midi', { method: 'POST', body: { folder: 'melodies', name: 'hum-1', tempo: 100, notes } })) as { path: string };
  const second = (await api('/api/hum/save-midi', { method: 'POST', body: { folder: 'melodies', name: 'hum-1', tempo: 100, notes } })) as { path: string };
  expect(first.path).toBe('melodies/hum-1.mid');
  expect(second.path).toBe('melodies/hum-1-2.mid');
});
