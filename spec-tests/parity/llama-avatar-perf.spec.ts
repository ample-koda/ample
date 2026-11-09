import { test, expect } from '@playwright/test';
import { freshServer, node, openApp } from '../../web/tests/support';

// Delay Llama avatars must not slow the page down (reported on a phone). Measured on a phone-sized page with the CPU slowed four times, nothing playing:
// before the fix one llama took the main thread from 7% to 33% busy and three took it to 53% (120 forced layouts a second for each one, and four stacked filters on a picture that moves).
const SONG = `composition "demo" { ample 1  tempo 120  master-seed 1 }
midi-clip line at 1 for 2 {
  bars 2
  notes 1:0:4:57:100 2:0:4:64:100
}
delay-llama l1 {
  from line.notes -> notes
}
delay-llama l2 {
  from line.notes -> notes
}
delay-llama l3 {
  from line.notes -> notes
}
mixer all {
  from l1.audio -> audio1
  from l2.audio -> audio2
  from l3.audio -> audio3
}
master master {
  from all.audio -> audio
}
`;
freshServer({ song: SONG });

test('three llama avatars keep the main thread of a slow phone mostly free and do not force layouts every frame', async ({ page }) => {
  test.setTimeout(90000);
  await page.setViewportSize({ width: 390, height: 844 });
  await openApp(page, { reset: false });
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Performance.enable');
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
  for (const name of ['l1', 'l2', 'l3']) await node(page, name).locator('.studio-llama-node').evaluate((b: HTMLElement) => b.click());
  await expect(page.locator('.llama-avatar')).toHaveCount(3);
  await page.waitForTimeout(800);
  const read = async () => Object.fromEntries((await cdp.send('Performance.getMetrics')).metrics.map((m) => [m.name, m.value]));
  const before = await read();
  await page.waitForTimeout(3000);
  const after = await read();
  const busyPct = ((after.TaskDuration - before.TaskDuration) / 3) * 100;
  const layoutsPerSecond = (after.LayoutCount - before.LayoutCount) / 3;
  console.log(`llama perf: main thread ${busyPct.toFixed(1)}% busy, ${layoutsPerSecond.toFixed(0)} layouts a second`);
  expect(busyPct).toBeLessThan(35);          // it was 53
  expect(layoutsPerSecond).toBeLessThan(60); // it was 241
});
