const clamp = (value, low, high) => Math.max(low, Math.min(high, value));
const semitones = [65536, 69433, 73562, 77936, 82570, 87480, 92682, 98193, 104032, 110218, 116772, 123715];

self.onmessage = ({ data }) => {
  try {
    const channels = data.channels.map((buffer) => process(new Int16Array(buffer), data.cents, data.sampleRate, data.method));
    const buffers = channels.map((channel) => channel.buffer);
    self.postMessage({ channels: buffers }, buffers);
  } catch (error) { self.postMessage({ error: error.message || 'Local pitch processing failed.' }); }
};

function process(input, cents, rate, method) {
  const shift = clamp(Math.round(cents), -2400, 2400);
  if (!shift) return Float32Array.from(input, (sample) => sample / 32768);
  if (method === 'voice') throw new Error('The voice pitch method uses costly pitch-period detection; this block is server-rendered for preview.');
  const ratio = ratioCents(shift), stretchedLength = Math.min(0x3fffffff, Math.floor(input.length * ratio / 65536) + 2);
  const stretched = method === 'wsola' ? wsola(input, stretchedLength, Math.floor(65536 * 65536 / ratio), rate) : granular(input, stretchedLength, Math.floor(65536 * 65536 / ratio), rate);
  const output = new Float32Array(input.length);
  for (let i = 0; i < output.length; i++) {
    const position = i * ratio, index = Math.floor(position / 65536), fraction = position & 65535;
    if (index + 1 >= stretched.length) break;
    output[i] = Math.floor((stretched[index] * (65536 - fraction) + stretched[index + 1] * fraction) / 65536) / 32768;
  }
  return output;
}

function ratioCents(cents) {
  let whole = Math.trunc(cents / 100), remainder = cents % 100;
  if (remainder < 0) { remainder += 100; whole--; }
  const low = ratio(whole), high = ratio(whole + 1);
  return low + Math.trunc((high - low) * remainder / 100);
}
function ratio(semitone) {
  let octave = Math.trunc(semitone / 12), rest = semitone % 12;
  if (rest < 0) { rest += 12; octave--; }
  const base = semitones[rest];
  return octave >= 0 ? base * (2 ** octave) : Math.floor(base / (2 ** -octave));
}
function grainWindow(size) { return Int32Array.from({ length: size }, (_, i) => Math.trunc(32768 * Math.min(i, size - i) * 2 / size)); }
function granular(input, outputLength, speedQ16, rate) {
  const grain = Math.max(64, Math.trunc(rate / 20)), hop = Math.trunc(grain / 2), hopIn = Math.trunc(hop * speedQ16 / 65536), window = grainWindow(grain), output = new Int32Array(outputLength);
  for (let count = 0, out = 0; out < outputLength; count++, out += hop) {
    const source = count * hopIn;
    if (source >= input.length) break;
    for (let i = 0; i < grain && out + i < outputLength; i++) if (source + i < input.length) output[out + i] += (input[source + i] * window[i]) >> 15;
  }
  return output;
}
function wsola(input, outputLength, speedQ16, rate) {
  const grain = Math.max(256, Math.trunc(rate / 25)), hop = Math.trunc(grain / 2), reach = Math.max(16, Math.trunc(rate / 200)), step = 4, hopIn = Math.trunc(hop * speedQ16 / 65536), window = grainWindow(grain), output = new Int32Array(outputLength);
  let previous = 0, first = true;
  for (let count = 0, out = 0; out < outputLength; count++, out += hop) {
    const nominal = count * hopIn;
    if (nominal >= input.length) break;
    let best = nominal;
    if (!first) {
      const target = previous + hop;
      let bestScore = -Infinity;
      for (let candidate = Math.max(0, nominal - reach); candidate <= nominal + reach; candidate++) {
        let score = 0;
        for (let i = 0; i < grain / 2; i += step) {
          const a = target + i, b = candidate + i;
          if (a >= input.length || b >= input.length) break;
          score += input[a] * input[b];
        }
        if (score > bestScore) { bestScore = score; best = candidate; }
      }
    }
    first = false; previous = best;
    for (let i = 0; i < grain && out + i < outputLength; i++) if (best + i < input.length) output[out + i] += (input[best + i] * window[i]) >> 15;
  }
  return output;
}
