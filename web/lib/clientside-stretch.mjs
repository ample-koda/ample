export function stretchWorkingSetBytes(frames, channels) { return frames * channels * 12; }

export async function processStretchChannels(inputChannels, sampleRate, params, signal) {
  const speed = Number(params.speed ?? '100');
  if (!Number.isFinite(speed)) throw new Error('Automated or invalid stretch speed uses the server renderer.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-stretch-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local stretch preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local stretch preview failed.'));
      const channels = inputChannels.map((input) => {
        const pcm = new Int16Array(input.length);
        for (let i = 0; i < input.length; i++) pcm[i] = Math.round(Math.max(-1, Math.min(1, input[i])) * (input[i] < 0 ? 32768 : 32767));
        return pcm;
      });
      const buffers = channels.map((channel) => channel.buffer);
      worker.postMessage({ channels: buffers, speed, sampleRate }, buffers);
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}
