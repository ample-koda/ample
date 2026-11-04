import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// Koda pointed at a selection of several lines in the source editor knows it points at that whole selection (and the selection stays), not at the single line under the pointer.
const SONG = `composition "demo" { ample 1  tempo 120  master-seed 1 }
midi-clip line at 1 for 2 {
  bars 2
  notes 1:0:4:57:100 2:0:4:64:100
}
delay-llama llama {
  vowel auto(1:100, 5:0)
  mouth 70
  from line.notes -> notes
}
master master {
  from llama.audio -> audio
}
`;
freshServer({ song: SONG });

test('pointing Koda at selected lines targets the whole selection', async ({ page }) => {
  await page.setViewportSize({ width: 1300, height: 900 });
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  // the list of files fills a moment after the view opens: try again until the song is in it
  await expect(async () => { await page.locator('#se-switch').fill('song.amplet'); await page.locator('#se-switchlist [role="option"]', { hasText: 'song.amplet' }).first().click({ timeout: 3000 }); }).toPass({ timeout: 25000 });
  await expect.poll(() => srcText(page)).toContain('delay-llama');
  const result = await page.evaluate(async () => {
    const gaze = await import('/app/koda-gaze.js');
    const ta = document.querySelector('#se-ta') as HTMLTextAreaElement;
    const from = ta.value.indexOf('delay-llama'), to = ta.value.indexOf('from line.notes') + 'from line.notes -> notes'.length;
    ta.focus(); ta.setSelectionRange(from, to);
    const point = gaze.linePoint(7);   // the line "mouth 70", inside the selection
    if (!point) return { error: 'no point' };
    const inside = gaze.targetAt(point.x + 40, point.y);
    const outside = gaze.targetAt(point.x + 40, gaze.linePoint(1)!.y);   // "bars 2": not selected
    const subject = gaze.lookAt(inside, false);
    return { inside, outside: outside?.kind, kept: [ta.selectionStart === from, ta.selectionEnd === to], subject: JSON.stringify(subject).slice(0, 300) };
  });
  expect((result as any).inside.kind).toBe('selection');
  expect((result as any).inside.endLine - (result as any).inside.startLine).toBeGreaterThanOrEqual(3);
  expect((result as any).outside).toBe('line');
  expect((result as any).kept).toEqual([true, true]);
  expect((result as any).subject).toContain('vowel');
});
