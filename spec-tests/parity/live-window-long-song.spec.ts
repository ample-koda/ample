import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// A song longer than the live window: the live chain plays a window of N bars that follows the loop range of the chain bar (and the step buttons), not always bars 1-N.
// 60 bpm makes a bar 4 s, so the 128 MiB cap leaves a window of 28 bars; the song has 60. The bass line changes at bar 31 and the volume ramps over the whole song.
const song = `composition "Long window" { ample 1  tempo 60  master-seed 5 }

sequencer sq at 1 for 60 {
  lanes lane1=bass
  sequence {
    sequencer 1
    channel bass bass vol=-6
    pattern verse bars=2
      bass 1:0:1:45:100 1:2:1:45:90 2:0:1:43:100 2:2:1:43:90
    pattern chorus bars=2
      bass 1:0:2:41:100 2:0:2:48:100
    playlist
      clip verse track=1 at=1 for=30
      clip chorus track=1 at=31 for=30
  }
}

volume vol {
  gain auto(1:-40, 60:0)
  from sq.lane1 -> audio
}

master master bake {
  from vol.audio -> audio
}
`;
freshServer({ song });

const state = (page) => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
const info = async (page) => (await state(page)).items.find((i: any) => i.id === 'sq:lane1').info as string;

test('@audio the window of a long song follows the step buttons and the loop range: badge, notes and gain are those of its bars', async ({ page }) => {
  await openApp(page, { reset: false });
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('vol'))).toBe(true);
  expect((await state(page)).window).toEqual({ first: 1, bars: 28, songBars: 60 });
  expect(await info(page)).toContain('previewing bars 1-28 of 60');

  // the right step button moves the window by half its length (the loop range is the whole take); the badge, the notes and the gain follow
  await page.locator('.livechain .lcstep').nth(1).click();
  await expect.poll(async () => (await state(page)).window.first).toBe(15);
  await page.locator('.livechain .lcstep').nth(1).click();
  await page.locator('.livechain .lcstep').nth(1).click();
  await expect.poll(async () => (await state(page)).window.first).toBe(33);   // 15 + 14 = 29, then the last full window: 60 - 28 + 1
  await expect.poll(() => info(page), { timeout: 15000 }).toContain('previewing bars 33-60 of 60');
  await expect(page.locator('.livechain .lcstep').nth(1)).toBeDisabled();

  // the notes of the window are right for its bars: a second render that starts two bars earlier (bar 31, the chorus too) has the same sound at the same song bars (a whole-song render is over the memory budget, which is why there is a window)
  const same = () => page.evaluate(async () => {
    const chain = await import('/app/live-chain.js'), { flat } = await import('/app/store.js');
    const { sequencePreviewPlan, renderSequenceChannels } = await import('/lib/clientside-sequencer.mjs'), { renderSynthChannels } = await import('/lib/clientside-synth.mjs');
    const first = chain.chainState().window.first, buffer = chain.chainBuffer('sq:lane1'), per = 176400;
    if (!buffer || buffer.length !== 28 * per) return { ready: false };
    const params = flat().nodes.find((n: any) => n.id === 'sq').params;
    const other = (await renderSequenceChannels(sequencePreviewPlan(params.sequence, params.lanes, 31, 28), params, renderSynthChannels, 44100, 31, 28, per, new AbortController().signal)).lane1.channels[0];
    const got = buffer.getChannelData(0).subarray(0, 26 * per), want = other.subarray((first - 31) * per, (first - 31 + 26) * per);
    let worst = 0, peak = 0;
    for (let i = 0; i < got.length; i++) { worst = Math.max(worst, Math.abs(got[i] - want[i])); peak = Math.max(peak, Math.abs(want[i])); }
    return { ready: true, first, worst, peak };
  });
  await expect.poll(async () => { const r = await same(); return r.ready ? r.worst < 1e-6 && r.peak > 0.001 : false; }, { timeout: 30000 }).toBe(true);

  // the volume ramp is read at the song position: bar 33 of 60 is far into the ramp, not at its start (-40 dB)
  const gain = await page.evaluate(async () => {
    const chain = await import('/app/live-chain.js'), { gainAtSeconds } = await import('/lib/live-auto.mjs'), now = chain.chainState();
    return { got: Number.parseFloat(chain.chainGain('vol')), want: gainAtSeconds('auto(1:-40, 60:0)', now.position + (now.window.first - 1) * 4, 4) };
  });
  expect(gain.want).toBeGreaterThan(-20);
  expect(Math.abs(gain.got - gain.want)).toBeLessThan(1.5);

  // a 4-bar loop range at the end of the song keeps the last full window
  await page.locator('.livechain .lcloop').selectOption('4');
  const after = await state(page);
  expect(after.window.first).toBe(33);   // the song ends at bar 60: the last full window is the only one that fits at or after bar 33
  expect(after.region.length).toBeCloseTo(16, 5);
  await expect.poll(() => info(page), { timeout: 15000 }).toContain('previewing bars 33-60 of 60');

  // back to the first bars with the left button (steps of 14 bars), then a 4-bar range at the playhead starts the window at that bar
  await page.locator('.livechain .lcloop').selectOption('0');
  for (let k = 0; k < 8; k++) if ((await state(page)).window.first > 1) await page.locator('.livechain .lcstep').nth(0).click();
  await expect.poll(async () => (await state(page)).window.first, { timeout: 15000 }).toBe(1);
  await page.locator('.livechain .lcloop').selectOption('4');
  const ranged = (await state(page)).window.first;   // the playhead's bar, 1 to 28: the window starts there
  expect(ranged).toBeGreaterThanOrEqual(1);
  expect(ranged).toBeLessThanOrEqual(28);
  await expect.poll(async () => (await state(page)).window.first).toBe(ranged);
  await expect.poll(() => info(page), { timeout: 15000 }).toContain(`previewing bars ${ranged}-${ranged + 27} of 60`);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => { m.setLoopBars(0); m.stopChain(); }));
});
