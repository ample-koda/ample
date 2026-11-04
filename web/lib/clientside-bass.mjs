export function bassWorkingSetBytes(frames) { return frames * 8; }

export async function renderBassChannels(notes, sampleRate, firstBar, bars, samplesPerBar, signal) {
  const frames = bars * samplesPerBar;
  if (!Number.isSafeInteger(frames) || frames < 1 || bassWorkingSetBytes(frames) > 128 * 1024 * 1024) throw new Error('Bass preview exceeds its 128 MiB working-set limit.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-bass-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local bass preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(new Float32Array(data.channel));
      worker.onerror = (event) => reject(new Error(event.message || 'The local bass preview failed.'));
      worker.postMessage({ notes, sampleRate, firstBar, bars, samplesPerBar });
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}
