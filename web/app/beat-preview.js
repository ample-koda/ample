// The preview of a beat in the wizard: the drum grid played in the browser on the samples of the chosen kit, in a loop, with a tempo and swing you can change while it plays.
// Nothing is built or stored; the song is not touched. @ample:beats
import { liveAudioContext } from './live-play.js';
import { stepGain } from '../lib/beat-model.mjs';
import { barBeats } from '../lib/meter.mjs';

const buffers = new Map();   // the samples that were loaded, by their path

async function loadSample(ctx, path) {
  if (!buffers.has(path)) {
    const response = await fetch(`/api/project/audio?path=${encodeURIComponent(path)}`);
    if (!response.ok) throw new Error(`The sample ${path} could not be read.`);
    buffers.set(path, await ctx.decodeAudioData(await response.arrayBuffer()));
  }
  return buffers.get(path);
}

/**
 * entries: [{ voice, steps, mods }] on the voices of the kit; fileOf(voice) gives the path of the sample of a voice.
 * Returns { start(), stop(), set({ bpm, swing }), playing } ; start() rejects when a sample cannot be read.
 */
export function createBeatPreview({ entries, fileOf, bpm = 100, swing = 0 }) {
  let live = { bpm, swing }, timer = 0, playing = false, step = 0, next = 0;
  const voices = [];
  const preview = {
    get playing() { return playing; },
    set(values) { live = { ...live, ...values }; },
    async start() {
      if (playing) return;
      const ctx = liveAudioContext();
      voices.length = 0;
      for (const e of entries) {
        const path = fileOf(e.voice);
        if (path) voices.push({ e, buffer: await loadSample(ctx, path) });
      }
      playing = true; step = 0; next = ctx.currentTime + 0.08;
      const length = Math.max(1, ...entries.map((e) => e.steps.length));
      const tick = () => {
        while (next < ctx.currentTime + 0.14) {
          const barSeconds = (barBeats() * 60) / live.bpm, stepSeconds = barSeconds / length;
          // swing: every second step comes a little later
          const late = length % 2 === 0 && step % 2 === 1 ? (live.swing / 100) * stepSeconds * 0.66 : 0;
          for (const { e, buffer } of voices) {
            const gain = stepGain(e.steps[step % e.steps.length], e.mods);
            if (!gain) continue;
            const source = ctx.createBufferSource(), level = ctx.createGain();
            source.buffer = buffer; level.gain.value = Math.min(1, gain);
            source.connect(level).connect(ctx.destination);
            source.start(next + late);
          }
          next += stepSeconds; step = (step + 1) % length;
        }
      };
      timer = setInterval(tick, 25);
      tick();
    },
    stop() { clearInterval(timer); playing = false; },
  };
  return preview;
}
