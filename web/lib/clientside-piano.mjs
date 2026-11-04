export function pianoWorkingSetBytes(frames) { return frames * 8; }

export async function renderPianoChannels(notes, sampleRate, firstBar, bars, samplesPerBar, signal) {
  const frames = bars * samplesPerBar;
  if (!Number.isSafeInteger(frames) || frames < 1 || pianoWorkingSetBytes(frames) > 128 * 1024 * 1024) throw new Error('Piano preview exceeds its 128 MiB working-set limit.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-piano-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local piano preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(new Float32Array(data.channel));
      worker.onerror = (event) => reject(new Error(event.message || 'The local piano preview failed.'));
      worker.postMessage({ notes, sampleRate, firstBar, bars, samplesPerBar });
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}
