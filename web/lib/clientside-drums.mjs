import { barBeats } from './meter.mjs';
export function drumWorkingSetBytes(frames) { return frames * 6; }

export function builtinDrumSongSupported(params) {
  const unquote = (value) => String(value || '').trim().replace(/^"+|"+$/g, '').trim();
  if (unquote(params.patterns)) return false;
  // the browser plays beat-A and beat-B at the usual steps of a bar; a grid in the pattern parameter, or another steps parameter, is read by the server only
  const pattern = unquote(params.pattern);
  if (pattern && pattern !== 'beat-A' && pattern !== 'beat-B') return false;
  const steps = unquote(params.steps);
  if (steps && Number(steps) !== Math.max(1, Math.round(4 * barBeats()))) return false;
  const sequence = unquote(params.song).split(/[\s,]+/).filter(Boolean);
  return sequence.every((entry) => {
    const [name, count, ...extra] = entry.split('*');
    return extra.length === 0 && ['pattern', 'beat-A', 'beat-B'].includes(name) && (count === undefined || /^\d+$/.test(count) && Number(count) >= 1 && Number(count) <= 999);
  });
}

export async function renderBuiltinDrumChannels(params, seed, sampleRate, firstBar, bars, activeBars, signal) {
  const frames = bars * Math.floor((barBeats() * 60 * sampleRate * 1000) / Math.round((Number(params.tempo) || 120) * 1000));
  if (!Number.isSafeInteger(frames) || frames < 1 || drumWorkingSetBytes(frames) > 128 * 1024 * 1024) throw new Error('Built-in drum preview exceeds its 128 MiB working-set limit.');
  if (!builtinDrumSongSupported(params)) throw new Error('The built-in drum preview supports beat-A/beat-B and their song sequence only.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-drums-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The built-in drum preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The built-in drum preview failed.'));
      worker.postMessage({ params, seed: String(seed), sampleRate, firstBar, bars, activeBars, beatsPerBar: barBeats() });
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}
