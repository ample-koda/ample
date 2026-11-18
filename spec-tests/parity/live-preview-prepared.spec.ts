import { test, expect, type Page } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

// Live Preview: Ctrl+Enter builds the target and keeps the build for Play. Listen uses it while the song and its files are what they were, and builds again when not.
freshServerPerTest();

const song = `composition "Prepared" { ample 1  tempo 120  master-seed 7 }
midi-clip theme at 1 for 4 {
  notes 1:0:1:60:100 1:1:1:64:100 1:2:1:67:100 2:0:2:72:100 3:0:1:60:100 4:0:4:64:100
}
synth lead {
  wave triangle
  gain -12 dB
  from theme.notes -> notes
}
master out {
  from lead.audio -> audio
}
`;

async function open(page: Page) {
  await openApp(page, { reset: false });
  await page.evaluate((text) => import('/app/store.js').then((s) => s.applyText(text)), song);
  let builds = 0;
  page.on('request', (request) => { if (request.url().endsWith('/api/build') && request.method() === 'POST') builds++; });
  await page.click('#vs-source');
  await expect(page.locator('#se-listen')).toBeVisible();
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => {
    const i = ta.value.indexOf('synth lead') + 8;
    ta.focus();
    ta.setSelectionRange(i, i);
    ta.onselectionchange?.(new Event('selectionchange'));
  });
  return () => builds;
}
const editor = (page: Page) => page.locator('#se-cm .cm-content');

test('Ctrl+Enter, then Listen: the build that was made plays, no second build', async ({ page }) => {
  const builds = await open(page);
  await editor(page).press('Control+Enter');
  await expect(page.locator('#toast')).toContainText('plays this build', { timeout: 60000 });
  expect(builds()).toBe(1);
  await editor(page).press('Control+Shift+Space');
  await expect(page.locator('#lp-status')).toContainText('hearing build #1', { timeout: 30000 });
  expect(builds(), 'the build that was made is the one that plays').toBe(1);
  await editor(page).press('Control+Shift+Space');
});

test('Ctrl+Enter, then a change of the song, then Listen: built again', async ({ page }) => {
  const builds = await open(page);
  await editor(page).press('Control+Enter');
  await expect(page.locator('#toast')).toContainText('plays this build', { timeout: 60000 });
  await page.locator('#se-ta').evaluate((ta: HTMLTextAreaElement) => { const i = ta.value.indexOf('gain -12 dB') + 'gain -1'.length; ta.focus(); ta.setSelectionRange(i, i + 1); });
  await editor(page).press('3');   // gain -13 dB
  await page.waitForTimeout(700);   // the text is applied
  await editor(page).press('Control+Shift+Space');
  await expect(page.locator('#lp-status')).toContainText('hearing build', { timeout: 60000 });
  expect(builds(), 'the old build was not used for the changed song').toBe(2);
  await editor(page).press('Control+Shift+Space');
});
