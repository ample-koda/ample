export function samplerWorkingSetBytes(frames, channels, sampleFrames, sampleChannels) { return frames * channels * 8 + sampleFrames * sampleChannels * 12; }

export async function renderSamplerChannels(sampleChannels, sourceRate, notes, params, sampleRate, firstBar, bars, samplesPerBar, signal) {
  const frames = bars * samplesPerBar, workingSet = samplerWorkingSetBytes(frames, sampleChannels.length, sampleChannels[0]?.length || 0, sampleChannels.length);
  if (!Number.isSafeInteger(frames) || frames < 1 || workingSet > 128 * 1024 * 1024) throw new Error('Sampler preview exceeds its 128 MiB working-set limit.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-sampler-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local sampler preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local sampler preview failed.'));
      const channels = sampleChannels.map((input) => input.slice());
      const buffers = channels.map((channel) => channel.buffer);
      worker.postMessage({ channels: buffers, sourceRate, notes, params, sampleRate, firstBar, bars, samplesPerBar }, buffers);
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}
