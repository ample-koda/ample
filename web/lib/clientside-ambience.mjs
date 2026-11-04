export function ambienceWorkingSetBytes(inputFrames, outputFrames, channels, offset) {
  return (inputFrames * channels * 4) + (Math.max(0, offset) + outputFrames) * channels * 8 + (outputFrames * channels * 4);
}

export function processAmbienceBuffer(inputChannels, params, seedValue, sampleRate, outputFrames, offset, signal) {
  if (!inputChannels.length || inputChannels.some((channel) => channel.length !== inputChannels[0].length) || !Number.isSafeInteger(outputFrames) || outputFrames < 0) throw new Error('The ambience source or output range is invalid.');
  const channels = inputChannels.length, frames = outputFrames, outputs = Array.from({ length: channels }, () => new Float32Array(frames));
  if (String(params.enabled ?? 'on') === 'off' || frames === 0) return outputs;
  const rate = Math.trunc(sampleRate), inputLength = inputChannels[0].length;
  const from = Math.min(inputLength, Math.round(Math.max(0, value(params.start, 0)) * rate));
  const requestedEnd = value(params.end, 0), to = requestedEnd > 0 ? Math.min(inputLength, Math.round(requestedEnd * rate)) : inputLength;
  const available = to - from;
  if (available < 256) throw new Error('The ambience source needs at least 256 samples after cropping.');
  const fade = clamp(Math.trunc(value(params.crossfade, 3) * rate), 64, Math.floor(available / 3));
  const segment = clamp(Math.trunc(value(params.segment, 12) * rate), fade * 2, available), variation = clamp(value(params.variation, 40), 0, 90) / 100;
  const mask = (1n << 64n) - 1n;
  let state = BigInt.asUintN(64, BigInt(seedValue)) || 0x2545F4914F6CDD1Dn;
  const next = () => {
    state = BigInt.asUintN(64, state ^ (state << 13n));
    state = BigInt.asUintN(64, state ^ (state >> 7n));
    state = BigInt.asUintN(64, state ^ (state << 17n));
    return Number(state >> 11n) / 9007199254740992;
  };
  const outputStart = Math.max(0, Math.trunc(offset)), visibleOffset = Math.max(0, -Math.trunc(offset));
  const need = Math.max(0, outputStart + frames - visibleOffset);
  if (need === 0) return outputs;
  const accumulators = Array.from({ length: channels }, () => new Float64Array(need));
  let written = 0, first = true;
  while (written < need) {
    signal?.throwIfAborted();
    const length = clamp(Math.trunc(segment * (1 + ((next() * 2) - 1) * variation)), fade * 2, available);
    const begin = from + Math.trunc(next() * Math.max(0, available - length));
    const at = first ? 0 : written - fade;
    for (let i = 0; i < length && at + i < need; i++) {
      if ((i & 0x3FFF) === 0) signal?.throwIfAborted();
      const gain = !first && i < fade ? Math.sin(Math.PI / 2 * i / fade) : 1;
      const tail = length - i <= fade ? Math.cos(Math.PI / 2 * (fade - (length - i)) / fade) : 1;
      const g = gain * tail, sourceIndex = begin + i;
      for (let channel = 0; channel < channels; channel++) accumulators[channel][at + i] += pcm(inputChannels[channel][sourceIndex]) * g;
    }
    written = at + length;
    first = false;
  }
  for (let frame = 0; frame < frames - visibleOffset; frame++) {
    if ((frame & 0x3FFF) === 0) signal?.throwIfAborted();
    for (let channel = 0; channel < channels; channel++) {
      const sample = clamp(roundEven(accumulators[channel][outputStart + frame]), -32768, 32767);
      outputs[channel][visibleOffset + frame] = sample < 0 ? sample / 32768 : sample / 32767;
    }
  }
  return outputs;
}

export async function processAmbienceChannels(inputChannels, params, seed, sampleRate, outputFrames, offset, signal) {
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-ambience-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local ambience preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local ambience preview failed.'));
      const channels = inputChannels.map((channel) => channel.slice());
      const transfers = channels.map((channel) => channel.buffer);
      worker.postMessage({ channels: transfers, params, seed: String(seed), sampleRate, outputFrames, offset }, transfers);
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}

const value = (input, fallback) => { const parsed = Number.parseFloat(String(input ?? '').trim()); return Number.isFinite(parsed) ? parsed : fallback; };
const clamp = (input, minimum, maximum) => Math.max(minimum, Math.min(maximum, input));
const pcm = (input) => Math.round(clamp(input * (input < 0 ? 32768 : 32767), -32768, 32767));
const roundEven = (input) => { const floor = Math.floor(input), fraction = input - floor; return fraction === .5 ? floor % 2 === 0 ? floor : floor + 1 : Math.round(input); };
