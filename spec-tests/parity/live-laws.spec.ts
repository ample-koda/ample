import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Plan local-preview 2.1: the WebAudio (L1) blend, pan and distortion laws agree with the offline JS port (L2), which mirrors the server.
freshServer();
test('mix, pan and distortion of the live graph match the offline port', async ({ page }) => {
  await openApp(page, { reset: false });
  const rows = await page.evaluate(async () => {
    const graph = await import('/app/studio/preview/audio.js');
    const dist = await import('/lib/clientside-distortion.mjs'), pan = await import('/lib/clientside-pan.mjs'), delay = await import('/lib/clientside-delay.mjs');
    const rate = 48000, frames = 24000, signal = new AbortController().signal;
    const sine = () => Float32Array.from({ length: frames }, (_, i) => 0.3 * Math.sin(2 * Math.PI * 440 * i / rate));
    const rms = (a: Float32Array) => { let s = 0; for (let i = 4800; i < a.length; i++) s += a[i] * a[i]; return Math.sqrt(s / (a.length - 4800)); };
    const db = (a: Float32Array) => 20 * Math.log10(Math.max(1e-9, rms(a)));
    const live = async (type: string, params: any, channels: Float32Array[], useLive = false) => {
      const ctx = new OfflineAudioContext(2, frames, rate); await graph.prepareWorklets(ctx); const buffer = ctx.createBuffer(2, frames, rate);
      buffer.copyToChannel(channels[0], 0); buffer.copyToChannel(channels[1] || channels[0], 1);
      const source = ctx.createBufferSource(); source.buffer = buffer;
      if (useLive) graph.liveEffectGraph(ctx, source, ctx.destination, type, params, 120, null); else graph.effectGraph(ctx, source, ctx.destination, type, params, 120, frames / rate);
      source.start(); const out = await ctx.startRendering();
      return [out.getChannelData(0), out.getChannelData(1)];
    };
    const out: any[] = [];
    for (const mix of [0, 25, 50, 75, 100]) {
      const params = { drive: '12', mode: 'soft', mix: String(mix), tone: '16000', level: '0' };
      const l1 = await live('distortion', params, [sine()]), l2 = await dist.distortionAudio({ sampleRate: rate, channels: [sine()] }, params, signal);
      out.push({ what: `distortion mix ${mix} (L1 ${db(l1[0]).toFixed(2)} dB, L2 ${db(l2.channels[0]).toFixed(2)} dB)`, diff: Math.abs(db(l1[0]) - db(l2.channels[0])) });
      for (const useLive of [true]) { const lv = await live('distortion', params, [sine()], useLive); out.push({ what: `live distortion mix ${mix} (L1 ${db(lv[0]).toFixed(2)} dB, L2 ${db(l2.channels[0]).toFixed(2)} dB)`, diff: Math.abs(db(lv[0]) - db(l2.channels[0])) }); }
      const dp = { time: '10', feedback: '40', mix: String(mix) };
      const d1 = await live('delay', dp, [sine()]), d2 = await delay.delayAudio({ sampleRate: rate, channels: [sine()] }, dp, signal);
      { const lv = await live('delay', dp, [sine()], true); out.push({ what: `live delay mix ${mix} (L1 ${db(lv[0]).toFixed(2)} dB, L2 ${db(d2.channels[0]).toFixed(2)} dB)`, diff: Math.abs(db(lv[0]) - db(d2.channels[0])) }); }
      out.push({ what: `delay mix ${mix} (L1 ${db(d1[0]).toFixed(2)} dB, L2 ${db(d2.channels[0]).toFixed(2)} dB)`, diff: Math.abs(db(d1[0]) - db(d2.channels[0])) });
    }
    for (const p of [-32, -16, 16, 32]) {
      const l1 = await live('pan', { pan: String(p) }, [sine(), sine()], true), l2 = await pan.panAudio({ sampleRate: rate, channels: [sine(), sine()] }, { pan: String(p) }, signal);
      for (const c of [0, 1]) if (rms(l2.channels[c]) > 1e-4) out.push({ what: `pan ${p} ch${c} (L1 ${db(l1[c]).toFixed(2)} dB, L2 ${db(l2.channels[c]).toFixed(2)} dB)`, diff: Math.abs(db(l1[c]) - db(l2.channels[c])) });
    }
    return out;
  });
  // Interior distortion mixes used to be up to 2.6 dB off (4x oversampler latency); the live distortion is now an AudioWorklet on the core of the offline render, so everything must be within 0.3 dB.
  for (const row of rows) expect.soft(row.diff, row.what).toBeLessThan(0.3);
});
