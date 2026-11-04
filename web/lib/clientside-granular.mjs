export function processGranularBuffer(inputChannels, rate, params, seed, signal) {
  const length = inputChannels[0].length, grain = Math.max(64, Math.trunc(clamp(value(params.grain, 120), 10, 1000) * rate / 1000));
  const density = clamp(value(params.density, 40), 1, 400), scatter = clamp(value(params.scatter, 60), 0, 100) / 100;
  const pitch = clamp(value(params.pitch, 0), 0, 12), outputs = inputChannels.map(() => new Float64Array(length));
  let state = BigInt.asUintN(64, BigInt(seed)) || 0x9E3779B97F4A7C15n;
  const next = () => { state = BigInt.asUintN(64, state ^ (state << 13n)); state = BigInt.asUintN(64, state ^ (state >> 7n)); state = BigInt.asUintN(64, state ^ (state << 17n)); return Number(state >> 11n) / 9007199254740992; };
  if (length <= grain * 2) return inputChannels.map((input) => input.slice());
  const spacing = Math.max(1, Math.trunc(rate / density)), scale = 1 / Math.sqrt(Math.max(1, grain / spacing));
  for (let at = 0; at < length; at += spacing) {
    if ((at & 0xFFFF) === 0) signal?.throwIfAborted();
    const centre = at + ((next() - .5) * 2 * scatter * length), speed = pitch === 0 ? 1 : 2 ** (((next() * 2) - 1) * pitch / 12);
    const from = clamp(centre - (grain * speed / 2), 0, length - 1 - (grain * speed));
    for (let i = 0; i < grain && at + i < length; i++) {
      const position = from + (i * speed), first = Math.trunc(position);
      if (first < 0 || first + 1 >= length) continue;
      const fraction = position - first, window = .5 - (.5 * Math.cos(2 * Math.PI * i / grain));
      for (let channel = 0; channel < inputChannels.length; channel++) {
        const input = inputChannels[channel], sample = inputPcm(input[first]) + ((inputPcm(input[first + 1]) - inputPcm(input[first])) * fraction);
        outputs[channel][at + i] += sample * window * scale;
      }
    }
  }
  return outputs.map((output) => Float32Array.from(output, (sample) => pcm(Math.round(sample)) / 32768));
}

export function parseEffectiveGranularSeed(body) { return /"seed"\s*:\s*\{[^}]*"value"\s*:\s*"?(\d+)"?/.exec(body)?.[1] ?? null; }

export function granularWorkingSetBytes(channels) { return channels.reduce((total, channel) => total + (channel.length * (Float64Array.BYTES_PER_ELEMENT + (2 * Float32Array.BYTES_PER_ELEMENT))), 0); }

export async function processGranularChannels(inputChannels, rate, params, seed, signal) {
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-granular-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local granular preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local granular preview failed.'));
      const channels = inputChannels.map((channel) => channel.slice());
      worker.postMessage({ channels: channels.map((channel) => channel.buffer), rate, params, seed: String(seed) }, channels.map((channel) => channel.buffer));
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}

function value(value, fallback) { const parsed = Number.parseFloat(value); return Number.isFinite(parsed) ? parsed : fallback; }
function clamp(number, minimum, maximum) { return Math.max(minimum, Math.min(maximum, number)); }
function pcm(number) { return Math.max(-32768, Math.min(32767, number)); }
function inputPcm(number) { return pcm(number * (number < 0 ? 32768 : 32767)); }
