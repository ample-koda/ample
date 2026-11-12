// The real sound of a General MIDI instrument for AMPLE Jam: a note (or a drum hit of the soundfont's drum kit) rendered by the server with the soundfont, bank and program of the channel,
// decoded once and kept, so a sound that was heard before plays at once and a new one plays as soon as it has arrived (after a short delay, so moving quickly over a list does not render everything).
import { api, getToken } from './api.js';

const projectList = async (folder) => { const r = await fetch(`/api/project/tree?path=${encodeURIComponent(folder)}&depth=0`, { headers: { 'X-Ample-Token': getToken() } }); return r.ok ? (await r.json()).children || [] : []; };
import { cacheFile, cacheable, sampleKey, sharedSampleCache } from '../lib/sf-sample-cache.mjs';
import { liveAudioContext } from './live-play.js';
import { playbackOutput } from './playback-volume.js';

const buffers = new Map();   // key -> decoded audio, shared by every window (the same soundfont, bank, program and pitch sound the same)
const loading = new Map();   // key -> promise while it is being rendered

/** @param {{ file?: string, bank?: number, program?: number }} instrument */
export function createJamInstrument(instrument) {
  const file = instrument.file || 'pack:musescore-general', bank = Number(instrument.bank ?? 0), program = Number(instrument.program ?? 0);
  let token = 0, current = null, timer = 0;
  const keyOf = (pitch, drum) => `${file}|${drum ? 'kit' : `${bank}:${program}`}|${pitch}`;
  function load(pitch, drum) {
    const key = keyOf(pitch, drum);
    if (buffers.has(key)) return Promise.resolve(buffers.get(key));
    if (!loading.has(key)) {
      loading.set(key, (async () => {
        // one note of a beat; a drum hit is the General MIDI drum channel (10) of the soundfont's kit
        // a pack's note rendered before (this session or an earlier one) is in the browser's cache: no round trip
        const cached = cacheable(file) ? await cacheFile(file, projectList).catch(() => null) : null, cacheKey = cached ? sampleKey({ file: cached, bank, program, channel: drum ? 9 : 0, drum, pitch, velocity: 100, ticks: 600 }) : null, cache = cacheKey ? await sharedSampleCache() : null;
        const stored = cache && await cache.get(cacheKey).catch(() => null);
        if (stored) { const buffer = await liveAudioContext().decodeAudioData(stored.slice(0)); buffers.set(key, buffer); return buffer; }
        const result = await api.gmAudition({ file, bank: drum ? 0 : bank, program: drum ? 0 : program, channel: drum ? 9 : 0, gain: 0, limiter: 'soft', notes: [{ bar: 1, tick: 0, ticks: 600, ppq: 480, pitch, velocity: 100 }] });
        const wav = await result.blob.arrayBuffer();
        if (cache) cache.put(cacheKey, wav.slice(0)).catch(() => {});
        const buffer = await liveAudioContext().decodeAudioData(wav);
        buffers.set(key, buffer);
        return buffer;
      })().finally(() => loading.delete(key)));
    }
    return loading.get(key);
  }
  function stop() {
    token++; clearTimeout(timer);
    if (!current) return;
    const ctx = liveAudioContext(), { source, gain } = current;
    current = null;
    try { gain.gain.cancelScheduledValues(ctx.currentTime); gain.gain.setTargetAtTime(0, ctx.currentTime, 0.02); source.stop(ctx.currentTime + 0.12); } catch { /* already stopped */ }
  }
  /** Plays a pitch: at once when it was heard before, else when it has arrived (the delay keeps a fast sweep over a list from rendering everything). Returns false when nothing could be heard. */
  function play(pitch, { drum = false, delay = 120 } = {}) {
    stop();
    const mine = token, ready = buffers.has(keyOf(pitch, drum));
    const start = async () => {
      try {
        const buffer = await load(pitch, drum);
        if (mine !== token) return;
        const ctx = liveAudioContext(), source = ctx.createBufferSource(), gain = ctx.createGain();
        source.buffer = buffer; gain.gain.value = 0.9;
        source.connect(gain).connect(playbackOutput(ctx));
        source.start();
        current = { source, gain };
        source.onended = () => { if (current?.source === source) current = null; };
      } catch { /* the server could not render it: the sketch voice stays the fallback */ }
    };
    if (ready) start(); else timer = setTimeout(start, delay);
  }
  return { play, stop };
}
