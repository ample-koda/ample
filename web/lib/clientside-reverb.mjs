import { ReverbStream } from './reverb-stream.mjs';

/** Bytes the reverb needs: the feed and the wet sum (8 bytes a sample each) and the output (4) per channel, and a little for the rings. */
export function reverbWorkingSetBytes(frames, channels) { return (frames * channels * 20) + 65536; }

/**
 * The room of the server, sample for sample (the core is reverb-stream.mjs, which the live AudioWorklet runs too): the input loses its lows (160 Hz), eight damped combs ring
 * in parallel and four all-passes smooth the tail; the dry sound stays and mix sets the level of the room. Integer arithmetic like the server.
 */
export function processReverbBuffer(inputChannels, params, sampleRate, signal) {
  const frames = inputChannels[0]?.length || 0;
  return inputChannels.map((input, channel) => {
    const stream = new ReverbStream(sampleRate, channel === 1 ? 1 : 0), output = new Float32Array(frames);
    stream.configure(params);
    for (let i = 0; i < frames; i++) {
      if ((i & 0x1FFF) === 0) signal?.throwIfAborted();
      output[i] = stream.process(input[i]);
    }
    return output;
  });
}

export async function processReverbChannels(inputChannels, params, sampleRate, signal) {
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-reverb-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local reverb preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local reverb preview failed.'));
      const channels = inputChannels.map((channel) => channel.slice());
      worker.postMessage({ channels: channels.map((channel) => channel.buffer), params, sampleRate }, channels.map((channel) => channel.buffer));
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}

