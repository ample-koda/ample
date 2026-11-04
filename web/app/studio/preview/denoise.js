const SIZE = 2048;
const HOP = SIZE / 4;

function fft(real, imaginary, inverse) {
  const length = real.length;
  for (let i = 1, j = 0; i < length; i++) {
    let bit = length >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) {
      [real[i], real[j]] = [real[j], real[i]];
      [imaginary[i], imaginary[j]] = [imaginary[j], imaginary[i]];
    }
  }

  for (let size = 2; size <= length; size <<= 1) {
    const angle = (inverse ? 2 : -2) * Math.PI / size;
    const stepReal = Math.cos(angle), stepImaginary = Math.sin(angle);
    for (let start = 0; start < length; start += size) {
      let wr = 1, wi = 0;
      for (let offset = 0; offset < size / 2; offset++) {
        const even = start + offset, odd = even + size / 2;
        const tr = (wr * real[odd]) - (wi * imaginary[odd]);
        const ti = (wr * imaginary[odd]) + (wi * real[odd]);
        real[odd] = real[even] - tr; imaginary[odd] = imaginary[even] - ti;
        real[even] += tr; imaginary[even] += ti;
        [wr, wi] = [(wr * stepReal) - (wi * stepImaginary), (wr * stepImaginary) + (wi * stepReal)];
      }
    }
  }

  if (inverse) for (let i = 0; i < length; i++) { real[i] /= length; imaginary[i] /= length; }
}

function frameAt(samples, window, real, imaginary, frame) {
  const start = (frame * HOP) - SIZE + HOP;
  for (let i = 0; i < SIZE; i++) {
    const index = start + i;
    real[i] = index >= 0 && index < samples.length ? samples[index] * window[i] : 0;
    imaginary[i] = 0;
  }
}

export function processDenoiseChannel(samples, sampleRate, params) {
  if (samples.length < SIZE) return samples.slice();
  // integer division, to match the server's frame count exactly (Math.ceil here would add an extra, mostly-padding frame on every non-exact length)
  const frames = Math.floor((samples.length + SIZE - 1) / HOP) + 1, bins = SIZE / 2 + 1;
  const window = Float64Array.from({ length: SIZE }, (_, i) => .5 - (.5 * Math.cos(2 * Math.PI * i / SIZE)));
  const real = new Float64Array(SIZE), imaginary = new Float64Array(SIZE), magnitudes = new Float32Array(frames * bins), noise = new Float64Array(bins);
  const fromStart = params.noise === 'start';
  if (fromStart) {
    const first = Math.min(Math.floor(Math.max(0, number(params.learn_from, 0)) * sampleRate / HOP), frames - 1);
    const last = Math.min(frames, first + Math.max(1, Math.floor(number(params.learn, .5) * sampleRate / HOP)));
    for (let frame = first; frame < last; frame++) {
      frameAt(samples, window, real, imaginary, frame); fft(real, imaginary, false);
      for (let bin = 0; bin < bins; bin++) noise[bin] += Math.hypot(real[bin], imaginary[bin]);
    }
    for (let bin = 0; bin < bins; bin++) noise[bin] /= Math.max(1, last - first);
  } else {
    for (let frame = 0; frame < frames; frame++) {
      frameAt(samples, window, real, imaginary, frame); fft(real, imaginary, false);
      for (let bin = 0; bin < bins; bin++) magnitudes[(frame * bins) + bin] = Math.hypot(real[bin], imaginary[bin]);
    }
    const column = new Float32Array(frames), rank = Math.floor(frames * .15);
    for (let bin = 0; bin < bins; bin++) {
      for (let frame = 0; frame < frames; frame++) column[frame] = magnitudes[(frame * bins) + bin];
      column.sort(); noise[bin] = column[rank];
    }
  }

  const floor = 10 ** (-clamp(number(params.reduction, 12), 0, 40) / 20), alpha = 1 + (clamp(number(params.sensitivity, 50), 0, 100) / 50);
  const previous = new Float64Array(bins).fill(1), output = new Float64Array(samples.length + (2 * SIZE));
  for (let frame = 0; frame < frames; frame++) {
    frameAt(samples, window, real, imaginary, frame); fft(real, imaginary, false);
    for (let bin = 0; bin < bins; bin++) {
      const magnitude = fromStart ? Math.hypot(real[bin], imaginary[bin]) : magnitudes[(frame * bins) + bin];
      let gain = magnitude <= 1e-9 ? floor : Math.max(floor, 1 - (alpha * noise[bin] / magnitude));
      gain = Math.max(gain, previous[bin] * .85); previous[bin] = gain;
      real[bin] *= gain; imaginary[bin] *= gain;
      if (bin > 0 && bin < SIZE / 2) { real[SIZE - bin] = real[bin]; imaginary[SIZE - bin] = -imaginary[bin]; }
    }
    fft(real, imaginary, true);
    const start = (frame * HOP) - SIZE + HOP;
    for (let i = 0; i < SIZE; i++) { const index = start + i; if (index >= 0 && index < samples.length) output[index] += real[i] * window[i]; }
  }
  return Float32Array.from(samples, (_, i) => Math.max(-1, Math.min(1, output[i] / 1.5)));
}

export async function processDenoiseChannels(inputChannels, sampleRate, params, signal) {
  signal?.throwIfAborted();
  const channels = inputChannels.map((channel) => channel.slice());
  const worker = new Worker(new URL('./denoise-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  const rendered = await new Promise((resolve, reject) => {
    abort = () => reject(new DOMException('The local denoise preview was cancelled.', 'AbortError'));
    signal?.addEventListener('abort', abort, { once: true });
    worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
    worker.onerror = (event) => reject(new Error(event.message || 'The local denoise preview failed.'));
    worker.postMessage({ channels: channels.map((channel) => channel.buffer), sampleRate, params }, channels.map((channel) => channel.buffer));
  }).finally(() => { signal?.removeEventListener('abort', abort); worker.terminate(); });
  return rendered;
}

export function denoiseWorkingSetBytes(channels) {
  const frames = Math.floor((channels[0].length + 2047) / 512) + 1;
  return channels.reduce((sum, channel) => sum + channel.byteLength + (channel.length * 12) + (frames * 1025 * 4), 0);
}

export async function renderDenoisePreview(input, params, signal) {
  const length = input.length, rendered = await processDenoiseChannels(Array.from({ length: input.numberOfChannels }, (_, channel) => input.getChannelData(channel).subarray(0, length)), input.sampleRate, params, signal);
  const bytes = new ArrayBuffer(44 + (length * rendered.length * 2)), view = new DataView(bytes), frameCount = length;
  const write = (offset, value) => { for (let i = 0; i < value.length; i++) view.setUint8(offset + i, value.charCodeAt(i)); };
  write(0, 'RIFF'); view.setUint32(4, bytes.byteLength - 8, true); write(8, 'WAVE'); write(12, 'fmt '); view.setUint32(16, 16, true);
  view.setUint16(20, 1, true); view.setUint16(22, rendered.length, true); view.setUint32(24, input.sampleRate, true);
  view.setUint32(28, input.sampleRate * rendered.length * 2, true); view.setUint16(32, rendered.length * 2, true); view.setUint16(34, 16, true);
  write(36, 'data'); view.setUint32(40, bytes.byteLength - 44, true);
  let offset = 44;
  for (let frame = 0; frame < frameCount; frame++) for (const channel of rendered) {
    const sample = Math.max(-1, Math.min(1, channel[frame])); view.setInt16(offset, sample < 0 ? sample * 32768 : sample * 32767, true); offset += 2;
  }
  return new Blob([bytes], { type: 'audio/wav' });
}

function number(value, fallback) { const parsed = Number.parseFloat(String(value ?? '')); return Number.isFinite(parsed) ? parsed : fallback; }
function clamp(value, minimum, maximum) { return Math.max(minimum, Math.min(maximum, value)); }
