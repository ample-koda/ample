import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// The "safe saves" setting: checkpoints around every save, named after what changed, and an autosave on its own.
freshServer({ song: 'composition "beats" { ample 1  tempo 100  master-seed 3 }\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\n' });

const messages = async () => ((await api('/api/history')).log as { message: string }[]).map((n) => n.message);

test('a save gets checkpoints named after the change, and unsaved work is autosaved', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  const off = await api('/api/project/auto-checkpoint');
  expect(off.enabled).toBe(false);
  expect(off.steps).toContain(10);
  await api('/api/project/auto-checkpoint', { method: 'POST', body: { enabled: true, minutes: 5 } });
  const doc = await api('/api/doc?path=song.amplet');
  const withBass = `${doc.text}bass bass at 1 for 4 {\n}\n`;
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text: withBass, baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  expect((await messages()).some((m) => m.startsWith('Saved song.amplet: added bass'))).toBe(true);
  // a wrong step is refused
  expect((await api('/api/project/auto-checkpoint', { method: 'POST', body: { minutes: 7 } })).enabled).toBeUndefined();
  expect((await api('/api/project/auto-checkpoint')).minutes).toBe(5);
});

test('unsaved changes are saved on their own after the interval, with checkpoints around it @slow', async ({ page }) => {
  test.setTimeout(150_000);   // the shortest interval is a minute (set AMPLE_AUTOSAVE_SECONDS_PER_MINUTE=1 for the server to make it a second)
  await openApp(page, { reset: false });
  await api('/api/project/auto-checkpoint', { method: 'POST', body: { enabled: true, minutes: 1 } });
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text: `${doc.text}# autosaved note\n`, baseVersion: doc.version } });
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).dirty, { timeout: 120_000 }).toBe(false);
  expect((await messages()).some((m) => m.startsWith('Autosave song.amplet'))).toBe(true);
});

test('saving a text file gets the same checkpoints as a song (issue 153)', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/project/auto-checkpoint', { method: 'POST', body: { enabled: true, minutes: 5 } });
  await api('/api/file', { method: 'PUT', body: { path: 'notes.txt', text: 'first
' } });
  await api('/api/wc', { method: 'PUT', body: { path: 'notes.txt', text: 'first
second
' } });
  await api('/api/wc/save', { method: 'POST', body: { path: 'notes.txt' } });
  const names = await messages();
  expect(names.some((m) => m.startsWith('Before saved of notes.txt'))).toBe(true);
  expect(names.some((m) => m.startsWith('Saved notes.txt'))).toBe(true);
  expect((await api('/api/file?path=notes.txt')).text).toBe('first
second
');
});

test('a save of a text file with the setting off makes no checkpoint, and suppressCheckpoint skips it', async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/project/auto-checkpoint', { method: 'POST', body: { enabled: false, minutes: 5 } });
  const before = (await messages()).length;
  await api('/api/wc', { method: 'PUT', body: { path: 'notes.txt', text: 'off
' } });
  await api('/api/wc/save', { method: 'POST', body: { path: 'notes.txt' } });
  expect((await messages()).length).toBe(before);
  await api('/api/project/auto-checkpoint', { method: 'POST', body: { enabled: true, minutes: 5 } });
  const counted = (await messages()).length;
  await api('/api/wc', { method: 'PUT', body: { path: 'notes.txt', text: 'quiet
' } });
  await api('/api/wc/save', { method: 'POST', body: { path: 'notes.txt', suppressCheckpoint: true } });
  expect((await messages()).length).toBe(counted);
});

test('unsaved changes of a text file are autosaved too @slow', async ({ page }) => {
  test.setTimeout(150_000);
  await openApp(page, { reset: false });
  await api('/api/project/auto-checkpoint', { method: 'POST', body: { enabled: true, minutes: 1 } });
  await api('/api/file', { method: 'PUT', body: { path: 'auto.txt', text: 'one
' } });
  await api('/api/wc', { method: 'PUT', body: { path: 'auto.txt', text: 'one
two
' } });
  await expect.poll(async () => (await api('/api/wc?path=auto.txt')).dirty, { timeout: 120_000 }).toBe(false);
  expect((await messages()).some((m) => m.startsWith('Autosave auto.txt'))).toBe(true);
});

test('the project settings have the switch and the slider with logical steps', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#open-settings').click();
  await page.click('#ps-tabbtn-saves');
  await page.locator('#ps-auto-on').check();
  await expect(page.locator('#ps-auto-minutes')).toBeEnabled();
  await page.locator('#ps-auto-minutes').fill('3');   // the 4th step
  await expect(page.locator('#ps-auto-shown')).toHaveText('10 min');
  await page.click('#ps-save-saves');   // each tab has its own Save
  await expect.poll(async () => (await api('/api/project/auto-checkpoint')).minutes).toBe(10);
  expect((await api('/api/project/auto-checkpoint')).enabled).toBe(true);
});
