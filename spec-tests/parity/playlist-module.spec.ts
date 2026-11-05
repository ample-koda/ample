import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

freshServer({ files: { 'modules/generated-midi.module': `module generated-midi version 1.0
output audio mix.mix

midi-clip clip at 1 for 1 {
  notes 1:0:1:60:80
}
gm instrument at 1 for 1 {
  from clip.notes -> notes
  program 0
}
mixer mix {
  from instrument.audio -> audio1
  gains 0 0 0 0
}
` } });

test('playlist module: a module opens from its line, shows its own lines, and the button leads back to the song', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('ample.playlist.on', '1'));
  await openApp(page);
  const doc = await api('/api/doc?path=song.amplet');
  const text = `${doc.text}\nuse generated-midi@1.0 as imported {\n}\nbus imported-bus {\n  from imported.audio -> audio1\n}\n`.replace('  from piano.audio -> audio3\n}', '  from piano.audio -> audio3\n  from imported-bus.audio -> audio4\n}');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text, baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await page.reload();
  await page.locator('#btabs button[data-t="playlist"]').click();
  await page.locator('#b-playlist .pl-line', { hasText: 'imported' }).locator('.pl-fold').click();
  await page.locator('#b-playlist .pl-sub[data-block="imported"] .pl-label').click({ button: 'right' });
  await page.locator('#menu .menu-item-with-icon', { hasText: 'Open the module' }).evaluate((el) => (el as HTMLElement).click());
  await expect(page.locator('#b-playlist .pl-toolbar b')).toHaveText('Module imported');
  await expect(page.locator('#b-playlist .pl-line .pl-name')).toHaveText(['mix']);   // the module has one mixer, the line of its instrument
  await page.locator('#b-playlist .pl-toolbar button', { hasText: '← Song' }).click();
  await expect(page.locator('#b-playlist .pl-toolbar b')).toHaveCount(0);
  await expect(page.locator('#b-playlist .pl-line .pl-name').first()).toHaveText('drums');
});
