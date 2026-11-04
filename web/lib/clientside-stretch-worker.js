self.onmessage = ({ data }) => {
  try {
    const channels = data.channels.map((buffer) => process(new Int16Array(buffer), data.speed, data.sampleRate));
    const buffers = channels.map((channel) => channel.buffer);
    self.postMessage({ channels: buffers }, buffers);
  } catch (error) { self.postMessage({ error: error.message || 'Local stretch processing failed.' }); }
};

function process(input, speed, rate) {
  const percent = Math.max(25, Math.min(400, Math.round(speed)));
  if (percent === 100) return Float32Array.from(input, (sample) => sample / 32768);
  const grain = Math.max(64, Math.trunc(rate / 20)), hop = Math.trunc(grain / 2), speedQ16 = Math.trunc(percent * 65536 / 100), hopIn = Math.trunc(hop * speedQ16 / 65536), window = new Int32Array(grain), output = new Int32Array(input.length);
  for (let i = 0; i < grain; i++) window[i] = Math.trunc(32768 * Math.min(i, grain - i) * 2 / grain);
  for (let count = 0, out = 0; out < output.length; count++, out += hop) {
    const source = count * hopIn;
    if (source >= input.length) break;
    for (let i = 0; i < grain && out + i < output.length; i++) if (source + i < input.length) output[out + i] += (input[source + i] * window[i]) >> 15;
  }
  return Float32Array.from(output, (sample) => Math.max(-32768, Math.min(32767, sample)) / 32768);
}
