import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Koda can be linked to the parts of the sequencer and of AMPLE Jam (the windows are stood in for by their classes).
freshServer();
test('a button of the sequencer and a tool of AMPLE Jam are things Koda can look at', async ({ page }) => {
  await openApp(page);
  await page.evaluate(() => {
    document.body.insertAdjacentHTML('beforeend', '<div class="sqmain" style="position:fixed;left:20px;top:200px;z-index:9999;background:#222;padding:10px"><div class="sqchannel"><input class="seqname" value="kick"><button id="t-mute" title="Mute this channel">M</button></div></div><div class="mls-panel" style="position:fixed;left:300px;top:200px;z-index:9999;background:#222;padding:10px"><button id="t-draw" class="mls-tool" title="Draw notes">Draw</button></div>');
  });
  const result = await page.evaluate(async () => {
    const g = await import('/app/koda-gaze.js');
    const at = (id: string) => { const r = document.getElementById(id)!.getBoundingClientRect(); return g.targetAt(r.left + 3, r.top + 3); };
    const mute = at('t-mute'), draw = at('t-draw');
    const sub = mute ? g.lookAt(mute) : null;
    return { mute, draw, sub };
  });
  expect(result.mute).toMatchObject({ kind: 'part', area: 'Sequencer', label: 'M', hint: 'Mute this channel', context: 'kick' });
  expect(result.draw).toMatchObject({ kind: 'part', area: 'AMPLE Jam', label: 'Draw' });
  expect(result.sub).toMatchObject({ kind: 'part', label: 'M' });
});
