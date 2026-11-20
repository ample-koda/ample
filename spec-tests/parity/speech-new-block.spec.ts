import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// A new speech block from the Studio starts with a sample sentence (the template default), never with an empty text line.
const SONG = `composition "demo" { ample 1  tempo 100  master-seed 1 }
master master {
}
`;
freshServer({ song: SONG });

const docText = async () => ((await api('/api/doc?path=song.amplet')) as { text: string }).text;

test('a new speech block from the Studio starts with a sample sentence and builds', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.edit([{ type: 'addBlock', blockType: 'speech', name: 'fresh' }]);
  });
  await expect.poll(docText).toMatch(/speech "?fresh"? {/);
  const text = await docText();
  expect(text).toMatch(/\n {2}text "I am a robot\./);
  expect(text).not.toMatch(/text ""|phonemes/);
  // the sentence is a normal text line: the block builds without a speech error
  const built = JSON.stringify(await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } }));
  expect(built).not.toContain('AMP5351');
});
