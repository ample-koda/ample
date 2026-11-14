// WAV helpers for plugins: read 16-bit PCM or 32-bit float WAV of one or two channels, mix to mono, resample (linear) and write 16-bit mono PCM.
export function parseWav(buf) {
  if (buf.length < 44 || buf.toString('ascii', 0, 4) !== 'RIFF' || buf.toString('ascii', 8, 12) !== 'WAVE') { throw new Error('the audio is not a WAV file'); }
  let pos = 12, fmt = null, data = null;
  while (pos + 8 <= buf.length) {
    const id = buf.toString('ascii', pos, pos + 4), size = buf.readUInt32LE(pos + 4), body = pos + 8;
    if (id === 'fmt ') { fmt = { code: buf.readUInt16LE(body), channels: buf.readUInt16LE(body + 2), rate: buf.readUInt32LE(body + 4), bits: buf.readUInt16LE(body + 14) }; }
    if (id === 'data') { data = buf.subarray(body, Math.min(body + size, buf.length)); }
    pos = body + size + (size % 2);
  }
  if (!fmt || !data) { throw new Error('the WAV file has no fmt or data chunk'); }
  const bytes = fmt.bits / 8, frames = Math.floor(data.length / (bytes * fmt.channels));
  const mono = new Float64Array(frames);
  for (let i = 0; i < frames; i++) {
    let sum = 0;
    for (let c = 0; c < fmt.channels; c++) {
      const at = (i * fmt.channels + c) * bytes;
      if (fmt.code === 1 && fmt.bits === 16) { sum += data.readInt16LE(at) / 32768; }
      else if (fmt.code === 3 && fmt.bits === 32) { sum += data.readFloatLE(at); }
      else { throw new Error(`unsupported WAV format (code ${fmt.code}, ${fmt.bits} bits)`); }
    }
    mono[i] = sum / fmt.channels;
  }
  return { rate: fmt.rate, samples: mono };
}

/** The channels of a WAV (one or two, as they are), each as floats between -1 and 1. */
export function parseWavChannels(buf) {
  const { fmt, data } = readChunks(buf);
  const bytes = fmt.bits / 8, frames = Math.floor(data.length / (bytes * fmt.channels));
  const channels = Array.from({ length: fmt.channels }, () => new Float64Array(frames));
  for (let i = 0; i < frames; i++) {
    for (let c = 0; c < fmt.channels; c++) {
      const at = (i * fmt.channels + c) * bytes;
      if (fmt.code === 1 && fmt.bits === 16) { channels[c][i] = data.readInt16LE(at) / 32768; }
      else if (fmt.code === 3 && fmt.bits === 32) { channels[c][i] = data.readFloatLE(at); }
      else { throw new Error(`unsupported WAV format (code ${fmt.code}, ${fmt.bits} bits)`); }
    }
  }
  return { rate: fmt.rate, channels };
}

function readChunks(buf) {
  if (buf.length < 44 || buf.toString('ascii', 0, 4) !== 'RIFF' || buf.toString('ascii', 8, 12) !== 'WAVE') { throw new Error('the audio is not a WAV file'); }
  let pos = 12, fmt = null, data = null;
  while (pos + 8 <= buf.length) {
    const id = buf.toString('ascii', pos, pos + 4), size = buf.readUInt32LE(pos + 4), body = pos + 8;
    if (id === 'fmt ') { fmt = { code: buf.readUInt16LE(body), channels: buf.readUInt16LE(body + 2), rate: buf.readUInt32LE(body + 4), bits: buf.readUInt16LE(body + 14) }; }
    if (id === 'data') { data = buf.subarray(body, Math.min(body + size, buf.length)); }
    pos = body + size + (size % 2);
  }
  if (!fmt || !data) { throw new Error('the WAV file has no fmt or data chunk'); }
  return { fmt, data };
}

/**
 * Converts a WAV to 16-bit PCM at the target rate and exactly the wanted number of samples, keeping its channels (one or two).
 * `mode`: 'auto' keeps what the audio has, 'mono' mixes to one channel, 'stereo' makes two channels also from a mono sound.
 * Returns an array of Int16Array, one per channel.
 */
export function toChannels16(buf, targetRate, wantedSamples, keepSamples, mode = 'auto') {
  const { rate, channels } = parseWavChannels(buf);
  let source = channels;
  if (mode === 'mono' && channels.length > 1) {
    const mixed = new Float64Array(channels[0].length);
    for (let i = 0; i < mixed.length; i++) { let sum = 0; for (const c of channels) { sum += c[i]; } mixed[i] = sum / channels.length; }
    source = [mixed];
  } else if (mode === 'stereo' && channels.length === 1) {
    source = [channels[0], channels[0]];
  } else if (channels.length > 2) {
    source = channels.slice(0, 2);   // more than two channels: the first two
  }
  return source.map((samples) => resample16(samples, rate, targetRate, wantedSamples, keepSamples));
}

function resample16(samples, rate, targetRate, wantedSamples, keepSamples) {
  const n = wantedSamples ?? Math.round(samples.length * targetRate / rate);
  const out = new Int16Array(n);
  for (let i = 0; i < n; i++) {
    const t = i * rate / targetRate, i0 = Math.floor(t), f = t - i0;
    if (i0 >= samples.length) { break; }
    const a = samples[i0], b = i0 + 1 < samples.length ? samples[i0 + 1] : a;
    out[i] = Math.max(-32768, Math.min(32767, Math.round((a + (b - a) * f) * 32767)));
  }
  if (keepSamples != null && keepSamples < n) {
    const fade = Math.min(Math.round(targetRate * 0.08), keepSamples);
    for (let i = 0; i < fade; i++) { const at = keepSamples - fade + i; out[at] = Math.round(out[at] * (1 - (i + 1) / fade)); }
    out.fill(0, keepSamples);
  }
  return out;
}

/** A 16-bit PCM WAV of one or two channels (arrays of Int16 of the same length). */
export function wavChannels(channels, rate) {
  const count = channels.length, frames = channels[0].length, size = frames * count * 2;
  const buf = Buffer.alloc(44 + size);
  buf.write('RIFF', 0); buf.writeInt32LE(36 + size, 4); buf.write('WAVEfmt ', 8);
  buf.writeInt32LE(16, 16); buf.writeInt16LE(1, 20); buf.writeInt16LE(count, 22); buf.writeInt32LE(rate, 24);
  buf.writeInt32LE(rate * count * 2, 28); buf.writeInt16LE(count * 2, 32); buf.writeInt16LE(16, 34); buf.write('data', 36); buf.writeInt32LE(size, 40);
  for (let i = 0; i < frames; i++) { for (let c = 0; c < count; c++) { buf.writeInt16LE(channels[c][i], 44 + (i * count + c) * 2); } }
  return buf;
}

/** Converts any supported WAV to 16-bit mono PCM at the target rate and exactly the wanted number of samples (cut or padded with silence). */
/** keepSamples (optional): only this many samples are sound, with a short fade at the end; the rest of wantedSamples is silence. */
export function toMono16(buf, targetRate, wantedSamples, keepSamples) {
  const { rate, samples } = parseWav(buf);
  const n = wantedSamples ?? Math.round(samples.length * targetRate / rate);
  const out = new Int16Array(n);
  for (let i = 0; i < n; i++) {
    const t = i * rate / targetRate, i0 = Math.floor(t), f = t - i0;
    if (i0 >= samples.length) { break; }
    const a = samples[i0], b = i0 + 1 < samples.length ? samples[i0 + 1] : a;
    out[i] = Math.max(-32768, Math.min(32767, Math.round((a + (b - a) * f) * 32767)));
  }
  if (keepSamples != null && keepSamples < n) {
    const fade = Math.min(Math.round(targetRate * 0.08), keepSamples);
    for (let i = 0; i < fade; i++) { const at = keepSamples - fade + i; out[at] = Math.round(out[at] * (1 - (i + 1) / fade)); }
    out.fill(0, keepSamples);
  }
  return out;
}
