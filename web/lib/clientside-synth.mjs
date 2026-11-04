export function synthWorkingSetBytes(frames, channels) { return frames * channels * 12; }

export async function renderSynthChannels(notes, params, sampleRate, firstBar, bars, samplesPerBar, signal) {
  const frames = bars * samplesPerBar;
  if (!Number.isSafeInteger(frames) || frames < 1 || synthWorkingSetBytes(frames, 2) > 128 * 1024 * 1024) throw new Error('Synth preview exceeds its 128 MiB working-set limit.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-synth-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local synth preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local synth preview failed.'));
      worker.postMessage({ notes, params, sampleRate, firstBar, bars, samplesPerBar });
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}
