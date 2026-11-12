import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 2.6: a send in the live graph gives a through sum and a send sum with a level per input, like the offline sendAudioBuffers (the L2 sum).
freshServer({ song: `${demoSong()}
send snd {
  levels -6 -12 -12 -12
  from drums.audio -> audio1
}
delay echo {
  from snd.send -> audio
}
` });
test('@audio the live send matches the offline send within 0.2 dB, through and send', async ({ page }) => {
  await openApp(page, { reset: false });
  const rows = await page.evaluate(async () => {
    const { sendGraph } = await import('/lib/live-mixer.mjs');
    const { sendAudioBuffers } = await import('/lib/clientside-mix.mjs');
    const rate = 48000, frames = rate, signal = new AbortController().signal;
    const tone = (hz: number, a: number) => Float32Array.from({ length: frames }, (_, i) => a * Math.sin(2 * Math.PI * hz * i / rate));
    const inputs = [tone(220, .3), tone(330, .2)];
    const db = (data: Float32Array) => { let s = 0; for (let i = 4800; i < data.length; i++) s += data[i] * data[i]; return 10 * Math.log10(Math.max(1e-12, s / (data.length - 4800))); };
    const out: any[] = [];
    for (const levels of ['-6 -12', '-12 -12', '0 -20', '-6']) {
      const context = new OfflineAudioContext(2, frames, rate), graph = sendGraph(context, 4);
      graph.update({ levels });
      inputs.forEach((data, k) => { const buffer = context.createBuffer(1, frames, rate); buffer.copyToChannel(data, 0); const source = context.createBufferSource(); source.buffer = buffer; source.connect(graph.lanes[k]); source.start(); });
      // the through output goes to the left channel, the send output to the right
      const through = context.createGain(), send = context.createGain(); graph.output.connect(through); graph.sendOutput.connect(send);
      const merge = context.createChannelMerger(2); through.connect(merge, 0, 0); send.connect(merge, 0, 1); merge.connect(context.destination);
      const live = await context.startRendering();
      const reference = await sendAudioBuffers(inputs.map((data, index) => ({ audio: { sampleRate: rate, channels: [data.slice()] }, index })), { levels, channels: 'stereo' }, signal);
      out.push({ what: `levels "${levels}" through`, diff: Math.abs(db(live.getChannelData(0)) - db(reference.through.channels[0])) });
      out.push({ what: `levels "${levels}" send`, diff: Math.abs(db(live.getChannelData(1)) - db(reference.send.channels[0])) });
    }
    return out;
  });
  for (const row of rows) expect.soft(row.diff, row.what).toBeLessThan(0.2);
});

test('@audio the send output feeds an effect in the live chain', async ({ page }) => {
  await openApp(page, { reset: false });
  const rate = 44_100, frames = rate * 2, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let f = 0; f < frames; f++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * f / rate) * 9000), 44 + f * 2);
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: bytes }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'drums' ? 'take-drums' : undefined) }), configurable: true }); }));
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('echo'))).toBe(true);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.items.map((i: any) => i.id)).toEqual(['drums', 'snd', 'echo']);
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('echo')?.left ?? 0)), { timeout: 8000 }).toBeGreaterThan(0.005);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
