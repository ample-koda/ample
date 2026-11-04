export function processConvolutionBuffer(inputChannels, impulse, params, sampleRate, signal) {
  const frames = inputChannels[0].length, channels = inputChannels.length, result = inputChannels.map(() => new Float32Array(frames));
  const block = nextPowerOfTwo(Math.max(impulse.length, 4096)), size = block * 2, filterReal = new Float64Array(size), filterImaginary = new Float64Array(size);
  filterReal.set(impulse); fft(filterReal, filterImaginary, false);
  const mix = clamp(value(params.mix, 30), 0, 100) / 100, level = 10 ** (value(params.level, 0) / 20), pre = Math.trunc(clamp(value(params.predelay, 0), 0, 500) * sampleRate / 1000);
  for (let channel = 0; channel < channels; channel++) {
    const input = inputChannels[channel], wet = new Float64Array(frames), real = new Float64Array(size), imaginary = new Float64Array(size);
    for (let start = 0; start < frames; start += block) {
      signal?.throwIfAborted(); real.fill(0); imaginary.fill(0);
      const count = Math.min(block, frames - start);
      for (let i = 0; i < count; i++) real[i] = inputPcm(input[start + i]);
      fft(real, imaginary, false);
      for (let i = 0; i < size; i++) { const r = (real[i] * filterReal[i]) - (imaginary[i] * filterImaginary[i]); imaginary[i] = (real[i] * filterImaginary[i]) + (imaginary[i] * filterReal[i]); real[i] = r; }
      fft(real, imaginary, true);
      for (let i = 0; i < size && start + i < frames; i++) wet[start + i] += real[i];
    }
    for (let i = 0; i < frames; i++) {
      if ((i & 0xFFFF) === 0) signal?.throwIfAborted();
      const wetIndex = i - pre, wetSample = wetIndex >= 0 ? wet[wetIndex] * level : 0;
      result[channel][i] = clipPcm(roundEven((inputPcm(input[i]) * (1 - mix)) + (wetSample * mix))) / 32768;
    }
  }
  return result;
}

export function convolutionWorkingSetBytes(frames, channels, impulseLength) {
  const size = nextPowerOfTwo(Math.max(impulseLength, 4096)) * 2;
  return (size * 32) + (frames * 8) + (frames * channels * 8) + (impulseLength * 4);
}

export function mapConvolutionImpulse(inputChannels, inputRate, targetRate, params) {
  const inputLength = inputChannels[0]?.length || 0, ratio = inputRate / targetRate;
  const length = Math.max(1, Math.min(Math.floor(clamp(value(params.length, 3), .05, 20) * targetRate), Math.ceil(inputLength / ratio)));
  const output = new Float32Array(length), count = inputChannels.length;
  if (!count) return output;
  let energy = 0;
  for (let index = 0; index < length; index++) {
    const position = index * ratio, first = Math.min(inputLength - 1, Math.floor(position)), next = Math.min(inputLength - 1, first + 1), fraction = position - first;
    for (const channel of inputChannels) output[index] += (channel[first] * (1 - fraction) + channel[next] * fraction) / count;
    energy += output[index] * output[index];
  }
  if (params.normalize !== 'off' && energy > 0) for (let index = 0; index < length; index++) output[index] /= Math.sqrt(energy);
  return output;
}

export async function processConvolutionChannels(inputChannels, impulse, params, sampleRate, signal) {
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-convolution-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local convolution preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local convolution preview failed.'));
      const channels = inputChannels.map((channel) => channel.slice()), response = impulse.slice();
      worker.postMessage({ channels: channels.map((channel) => channel.buffer), impulse: response.buffer, params, sampleRate }, [...channels.map((channel) => channel.buffer), response.buffer]);
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}

function fft(real, imaginary, inverse) {
  const length = real.length;
  for (let i = 1, j = 0; i < length; i++) { let bit = length >> 1; for (; j & bit; bit >>= 1) j ^= bit; j ^= bit; if (i < j) { [real[i], real[j]] = [real[j], real[i]]; [imaginary[i], imaginary[j]] = [imaginary[j], imaginary[i]]; } }
  for (let size = 2; size <= length; size <<= 1) {
    const angle = (inverse ? 2 : -2) * Math.PI / size, sr = Math.cos(angle), si = Math.sin(angle);
    for (let start = 0; start < length; start += size) { let wr = 1, wi = 0; for (let offset = 0; offset < size / 2; offset++) { const even = start + offset, odd = even + size / 2, tr = (wr * real[odd]) - (wi * imaginary[odd]), ti = (wr * imaginary[odd]) + (wi * real[odd]); real[odd] = real[even] - tr; imaginary[odd] = imaginary[even] - ti; real[even] += tr; imaginary[even] += ti; [wr, wi] = [(wr * sr) - (wi * si), (wr * si) + (wi * sr)]; } }
  }
  if (inverse) for (let i = 0; i < length; i++) { real[i] /= length; imaginary[i] /= length; }
}

function nextPowerOfTwo(number) { let size = 1; while (size < number) size *= 2; return size; }
function value(value, fallback) { const parsed = Number.parseFloat(value); return Number.isFinite(parsed) ? parsed : fallback; }
function clamp(number, minimum, maximum) { return Math.max(minimum, Math.min(maximum, number)); }
function inputPcm(number) { return clamp(number * (number < 0 ? 32768 : 32767), -32768, 32767); }
function clipPcm(number) { return clamp(number, -32768, 32767); }
function roundEven(number) { const lower = Math.floor(number), fraction = number - lower; return fraction < .5 ? lower : fraction > .5 ? lower + 1 : lower % 2 === 0 ? lower : lower + 1; }
