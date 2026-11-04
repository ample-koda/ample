import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Pressing play on an audio tile shows a shimmer over its waveform while the sound is loaded in the browser first.
freshServer();
test('the audio tile shimmers while its sound is loading and the shimmer goes when it plays', async ({ page }) => {
  await openApp(page);
  await page.evaluate(() => {
    document.body.insertAdjacentHTML('beforeend', '<div class="project-tile" id="t1" style="position:fixed;left:20px;top:200px;width:260px;z-index:9999"><div class="project-wave-seek" style="height:58px"></div></div>');
    document.getElementById('t1')!.classList.add('is-loading');
  });
  const after = await page.evaluate(() => { const s = getComputedStyle(document.querySelector('#t1 .project-wave-seek')!, '::after'); return { animation: s.animationName, content: s.content }; });
  expect(after.animation).toBe('tile-shimmer');
  expect(after.content).not.toBe('none');
  await page.evaluate(() => document.getElementById('t1')!.classList.remove('is-loading'));
  const gone = await page.evaluate(() => getComputedStyle(document.querySelector('#t1 .project-wave-seek')!, '::after').animationName);
  expect(gone).toBe('none');
});
