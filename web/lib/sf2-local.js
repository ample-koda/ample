// The General MIDI pack SoundFont in the browser: fetched once from the server (GET /api/soundfont/general-midi, 215 MB, kept in memory for the page) and rendered with sf2-voice.mjs. @ample:sf2
import { createSf2Synth, renderSf2Note, renderSf2Notes } from './sf2-voice.mjs';

const loaded = new Map();
const URL_PACK = '/api/soundfont/general-midi';
/** The SoundFont response: from the Cache API when it was downloaded before (215 MB: once per browser, not per page), else from the server (and stored, best effort). */
async function soundFontResponse(token) {
  let cache = null;
  try { cache = await caches.open('ample-soundfont-v1'); const hit = await cache.match(URL_PACK); if (hit) return hit; } catch { /* no Cache API (private window): download each time */ }
  const response = await fetch(URL_PACK, { headers: { 'X-Ample-Token': token } });
  if (response.ok && cache) { try { await cache.put(URL_PACK, response.clone()); } catch { /* quota */ } }
  return response;
}
/** { render({ program, bank, pitch, velocity, seconds, tail }) -> [left, right] } for a sample rate (a promise; the first call downloads the SoundFont). */
export function localSf2(rate, token) {
  if (!loaded.has(rate)) {
    loaded.set(rate, soundFontResponse(token).then(async (response) => {
      if (!response.ok) throw new Error('The General MIDI SoundFont is not available for the browser.');
      const synth = createSf2Synth(await response.arrayBuffer(), rate);
      return { render: (note) => renderSf2Note(synth, note), renderNotes: (notes, seconds, options) => renderSf2Notes(synth, notes, seconds, options) };
    }).catch((error) => { loaded.delete(rate); throw error; }));
  }
  return loaded.get(rate);
}
