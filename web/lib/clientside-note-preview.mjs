// Pure decisions of the single-note audition bridge (plan MLS03): which local renderer sounds a note, the bounded note cache,
// the voice pool, the level rule and the latency budget. The browser side is web/app/note-audition.js; renderers stay in clientside-*.mjs.

/** The preview level of playback (live-play's master gain). Audition never goes above it, so it is never louder than the song. */
export const PLAYBACK_LEVEL = 0.7;
export const MAX_VOICES = 8;
export const MIN_AUDITION_MS = 120;
export const MAX_AUDITION_MS = 4000;
export const CACHE_BYTES = 32 * 1024 * 1024;

const fallback = (reason) => ({ kind: 'fallback', local: false, reason });

/**
 * Which renderer sounds `note` for the routed instrument `route` ({ node, chain } from resolveRollInstrument).
 * kind is 'synth' | 'piano' | 'bass' | 'sampler' | 'guitar' | 'pitched-set' | 'llama' (the Delay Llama voice) | 'gm' (server SoundFont) | 'fallback' (sketch voice, shown as approximate).
 * Keep in step with ample/docs/clientside-preview/inventory.md.
 */
export function selectPreviewVoice(route, note = {}) {
  if (!route?.node) return fallback('no-instrument');
  const { tpl, params = {} } = route.node;
  if (tpl === 'delay-llama') return { kind: 'llama', local: true };   // the voice of the engine in the browser (it sings fine and bend itself)
  if (note.slide || note.port || note.rel !== undefined || note.cut !== undefined) return fallback('expression');
  if (tpl !== 'synth' && (note.pan || note.fine)) return fallback('expression');
  const humanize = Number(params.humanize || 0) !== 0;
  if (tpl === 'gm') return { kind: 'gm', local: false, reason: 'server-soundfont' };
  if (tpl === 'synth') return { kind: 'synth', local: true };
  if (tpl === 'sampler') return { kind: 'sampler', local: true };
  if (tpl === 'piano' && (!params.set || params.set === 'felt-piano') && !humanize) return { kind: 'piano', local: true };
  if (tpl === 'bass' && (!params.set || params.set === 'analog-808') && !humanize && (!params.pattern || params.pattern === 'bass-A')) return { kind: 'bass', local: true };
  if (tpl === 'guitar' || tpl === 'piano' || tpl === 'bass') {
    if (humanize) return fallback('humanize');
    return { kind: tpl === 'guitar' ? 'guitar' : 'pitched-set', local: true };
  }
  return fallback('unsupported');
}

/** The badge for what is heard: only the sketch voice is "approximate". */
export function previewBadge(selection, real) {
  if (selection.kind === 'fallback') return { approximate: true, label: 'approximate preview (sketch voice)' };
  if (!real) return { approximate: true, label: 'approximate preview (warming the real sound)' };
  return { approximate: false, label: selection.kind === 'gm' ? 'SoundFont preview' : 'instrument preview' };
}

/** A byte-capped least-recently-used cache; `sizeOf(value)` gives the bytes of a value. */
export function createNoteCache(maxBytes = CACHE_BYTES, sizeOf = (value) => value.length * value.numberOfChannels * 4) {
  const map = new Map();
  let bytes = 0;
  const drop = (key) => { bytes -= sizeOf(map.get(key)); map.delete(key); };
  return {
    get(key) { if (!map.has(key)) return undefined; const value = map.get(key); map.delete(key); map.set(key, value); return value; },
    has: (key) => map.has(key),
    set(key, value) {
      const size = sizeOf(value);
      if (size > maxBytes) return false;
      if (map.has(key)) drop(key);
      while (bytes + size > maxBytes && map.size) drop(map.keys().next().value);
      map.set(key, value); bytes += size; return true;
    },
    clear() { map.clear(); bytes = 0; },
    get size() { return map.size; },
    get bytes() { return bytes; },
  };
}

/** Which pitches to render ahead: those the clip uses first, then a window around the viewport, nearest the middle first. Never more than `limit`. */
export function warmOrder(usedPitches, viewTop, viewBottom, limit = 48) {
  const out = [], seen = new Set();
  const add = (pitch) => { if (pitch >= 0 && pitch <= 127 && !seen.has(pitch)) { seen.add(pitch); out.push(pitch); } };
  [...usedPitches].sort((a, b) => a - b).forEach(add);
  const low = Math.min(viewTop, viewBottom) - 12, high = Math.max(viewTop, viewBottom) + 12, mid = (low + high) / 2;
  const window = [];
  for (let pitch = Math.floor(low); pitch <= high; pitch++) window.push(pitch);
  window.sort((a, b) => Math.abs(a - mid) - Math.abs(b - mid)).forEach(add);
  return out.slice(0, limit);
}

/** Round velocity into a bucket so neighbouring velocities share a rendering. */
export const velocityBucket = (velocity) => Math.max(1, Math.min(127, Math.round((Number(velocity) || 100) / 16) * 16 || 16));

/** Duration of an audition in seconds: at least 120 ms so a bare click is audible, at most 4 s; `heldMs` is how long the key is down (0 for a click). */
export const auditionSeconds = (heldMs = 0) => Math.max(MIN_AUDITION_MS, Math.min(MAX_AUDITION_MS, heldMs)) / 1000;

/** The preview gain: never above the playback level. */
export const previewLevel = (requested = PLAYBACK_LEVEL, playback = PLAYBACK_LEVEL) => Math.max(0, Math.min(Number.isFinite(requested) ? requested : playback, playback));

/**
 * Tracks sounding preview voices. Each voice is { stop() }. At most `max` sound at once (the oldest is cut);
 * a `mono` start first cuts every earlier mono voice (dragging across rows retriggers monophonically).
 */
export function createVoicePool(max = MAX_VOICES) {
  const voices = new Map();   // id -> { handle, mono }, in start order
  let next = 1;
  const cut = (id) => { const voice = voices.get(id); if (!voice) return false; voices.delete(id); voice.handle.stop(); return true; };
  return {
    start(handle, { mono = false } = {}) {
      if (mono) for (const [id, voice] of [...voices]) if (voice.mono) cut(id);
      while (voices.size >= max) cut(voices.keys().next().value);
      const id = next++;
      voices.set(id, { handle, mono });
      return id;
    },
    release: cut,
    ended(id) { voices.delete(id); },
    cutAll() { for (const id of [...voices.keys()]) cut(id); },
    get size() { return voices.size; },
    ids: () => [...voices.keys()],
  };
}

/** Latency budget in ms from pointer-down to the scheduled start (decision 18): 40 with a warm cache, 15 through the sketch voice. */
export const latencyBudget = (real) => (real ? 40 : 15);

/** A rolling record of trigger latencies in ms, split by warm/cold. */
export function createLatencyProbe(keep = 200) {
  const samples = { real: [], fallback: [] };
  return {
    record(ms, real) { const list = real ? samples.real : samples.fallback; if (Number.isFinite(ms) && ms >= 0) { list.push(ms); if (list.length > keep) list.shift(); } },
    summary(real) {
      const list = [...(real ? samples.real : samples.fallback)].sort((a, b) => a - b);
      if (!list.length) return { count: 0, p50: 0, p95: 0, max: 0, withinBudget: true };
      const at = (q) => list[Math.min(list.length - 1, Math.ceil(q * list.length) - 1)];
      return { count: list.length, p50: at(0.5), p95: at(0.95), max: list[list.length - 1], withinBudget: at(0.95) <= latencyBudget(real) };
    },
  };
}
