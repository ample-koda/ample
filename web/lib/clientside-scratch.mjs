const DEFAULT_PATTERN = 'x.x.b...x.x.r...';

export function scratchWorkingSetBytes(frames, channels) {
  return frames * channels * 20;
}

export function processScratchBuffer(inputChannels, params, sampleRate, samplesPerBar, firstBar, signal) {
  const groups = parseGroups(params.pattern), bars = parseBars(params.bars), depth = Math.trunc(clamp(read(params.depth, 120), 10, 600) * sampleRate / 1000), mix = Math.trunc(clamp(read(params.mix, 100), 0, 100));
  return inputChannels.map((input) => {
    const frames = input.length, source = new Int32Array(frames), output = new Float32Array(frames);
    for (let i = 0; i < frames; i++) { if ((i & 0xFFFF) === 0) signal?.throwIfAborted(); source[i] = inputPcm(input[i]); output[i] = source[i] / 32768; }
    const edge = Math.max(1, Math.floor(sampleRate / 500));
    for (let barOffset = 0; barOffset * samplesPerBar < frames; barOffset++) {
      const songBar = firstBar + barOffset;
      if (bars.size && !bars.has(songBar)) continue;
      const grid = groups[(songBar - 1) % groups.length], steps = [12, 24, 32].includes(grid.length) ? grid.length : 16;
      for (let step = 0; step < steps; step++) {
        const move = grid[step % grid.length];
        if (!'xcbfr-'.includes(move)) continue;
        const start = barOffset * samplesPerBar + Math.floor(step * samplesPerBar / steps), end = barOffset * samplesPerBar + Math.floor((step + 1) * samplesPerBar / steps), length = end - start, half = Math.max(1, Math.floor(length / 2));
        const previous = grid[(step - 1 + grid.length) % grid.length], next = grid[(step + 1) % grid.length];
        const rampIn = step === 0 || !'xcbfr-'.includes(previous), rampOut = step === steps - 1 || !'xcbfr-'.includes(next);
        for (let i = 0; i < length && start + i < frames; i++) {
          if ((i & 0xFFFF) === 0) signal?.throwIfAborted();
          const triangle = i < half ? Math.trunc(depth * 256 * i / half) : Math.trunc(depth * 256 * (length - i) / Math.max(1, length - half));
          const position = move === 'x' || move === 'c' ? triangle : move === 'b' ? Math.trunc(depth * 256 * (length - i) / Math.max(1, length)) : move === 'f' ? Math.trunc(depth * 256 * i / Math.max(1, length)) : move === 'r' ? Number((BigInt(depth * 4 * 256) * BigInt(length - i) * BigInt(length - i)) / BigInt(Math.max(1, length) ** 2)) : 0;
          const open = move !== '-' && (move !== 'c' || Math.abs(i - half) > length / 8), amount = move === 'r' ? Math.floor(256 * (length - i) / Math.max(1, length)) : 256;
          const readPosition = start * 256 + position, whole = Math.floor(readPosition / 256), fraction = readPosition - whole * 256;
          const a = whole < frames ? source[whole] : 0, b = whole + 1 < frames ? source[whole + 1] : 0;
          const scratched = open ? Math.floor(Math.floor((a * (256 - fraction) + b * fraction) / 256) * amount / 256) : 0;
          const ramp = Math.min(256, Math.floor(Math.min(rampIn ? i : edge, rampOut ? length - 1 - i : edge) * 256 / edge));
          const wet = Math.floor((scratched * ramp + source[start + i] * (256 - ramp)) / 256);
          output[start + i] = clipPcm(Math.trunc((wet * mix + source[start + i] * (100 - mix)) / 100)) / 32768;
        }
      }
    }
    return output;
  });
}

export async function processScratchChannels(inputChannels, params, sampleRate, samplesPerBar, firstBar, signal) {
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-scratch-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local scratch preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local scratch preview failed.'));
      const channels = inputChannels.map((channel) => channel.slice());
      worker.postMessage({ channels: channels.map((channel) => channel.buffer), params, sampleRate, samplesPerBar, firstBar }, channels.map((channel) => channel.buffer));
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}

function parseGroups(text) { const groups = String(text || '').trim().replace(/^"|"$/g, '').split('|').map((group) => group.replaceAll(' ', '')).filter(Boolean); return groups.length ? groups : [DEFAULT_PATTERN]; }
function parseBars(text) { return new Set(String(text || '').trim().replace(/^"|"$/g, '').split(/[, ;]+/).map((item) => read(item, 0)).filter((bar) => bar > 0)); }
function read(text, fallback) { const match = /^[+-]?\d+/.exec(String(text || '').trim()); return match ? Number.parseInt(match[0], 10) : fallback; }
function clamp(number, minimum, maximum) { return Math.max(minimum, Math.min(maximum, number)); }
function inputPcm(number) { return Math.round(clamp(number * (number < 0 ? 32768 : 32767), -32768, 32767)); }
function clipPcm(number) { return clamp(number, -32768, 32767); }
