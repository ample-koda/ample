// The pure parts of microphone recording (plan X07): sample conversion, trim, latency offset, normalise, peaks and the 16-bit WAV encoder.
// Samples are Float32Array in -1..1 (mono). Nothing here touches the browser, so it is unit tested in Node.
// @ample:recorder

/** Float samples (-1..1) to 16-bit integers, clipped. */
export function toInt16(samples) {
  const out = new Int16Array(samples.length);
  for (let i = 0; i < samples.length; i++) out[i] = Math.max(-32768, Math.min(32767, Math.round(samples[i] * 32768)));
  return out;
}

/** A mono 16-bit PCM WAV file (header plus data) as bytes. */
export function encodeWav(samples, sampleRate) {
  const pcm = toInt16(samples);
  const bytes = new Uint8Array(44 + pcm.length * 2);
  const view = new DataView(bytes.buffer);
  const ascii = (at, text) => { for (let i = 0; i < text.length; i++) bytes[at + i] = text.charCodeAt(i); };
  ascii(0, 'RIFF'); view.setUint32(4, 36 + pcm.length * 2, true); ascii(8, 'WAVE');
  ascii(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true); view.setUint32(28, sampleRate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
  ascii(36, 'data'); view.setUint32(40, pcm.length * 2, true);
  for (let i = 0; i < pcm.length; i++) view.setInt16(44 + i * 2, pcm[i], true);
  return bytes;
}

/** The part of a recording between two fractions (0..1), at least one sample. */
export function trim(samples, startFraction, endFraction) {
  const start = Math.max(0, Math.min(samples.length - 1, Math.floor(startFraction * samples.length)));
  const end = Math.max(start + 1, Math.min(samples.length, Math.ceil(endFraction * samples.length)));
  return samples.slice(start, end);
}

/** Moves a recording earlier by the latency (ms): the first samples (the delay the monitoring added) are dropped. A negative latency pads silence. */
export function applyOffset(samples, offsetMs, sampleRate) {
  const shift = Math.round(offsetMs * sampleRate / 1000);
  if (shift === 0) return samples;
  if (shift > 0) return samples.slice(Math.min(shift, samples.length));
  const out = new Float32Array(samples.length - shift);
  out.set(samples, -shift);
  return out;
}

/** Scales so the loudest sample reaches the given peak (0..1); silence stays silence. */
export function normalize(samples, peak = 0.9) {
  let max = 0;
  for (let i = 0; i < samples.length; i++) max = Math.max(max, Math.abs(samples[i]));
  if (max === 0) return samples;
  const gain = peak / max;
  return samples.map((v) => v * gain);
}

/** Min and max per bucket (interleaved), for drawing a waveform. */
export function peaksOf(samples, buckets) {
  const out = new Float32Array(buckets * 2);
  for (let b = 0; b < buckets; b++) {
    const from = Math.floor((b * samples.length) / buckets), to = Math.max(from + 1, Math.floor(((b + 1) * samples.length) / buckets));
    let lo = 0, hi = 0;
    for (let i = from; i < to && i < samples.length; i++) { lo = Math.min(lo, samples[i]); hi = Math.max(hi, samples[i]); }
    out[b * 2] = lo; out[b * 2 + 1] = hi;
  }
  return out;
}

/** Joins the chunks the recorder delivered into one array. */
export function joinChunks(chunks) {
  const out = new Float32Array(chunks.reduce((n, c) => n + c.length, 0));
  let at = 0;
  for (const c of chunks) { out.set(c, at); at += c.length; }
  return out;
}

/** Drops the samples that were recorded before startFrame. firstFrame is the frame (on the audio clock) of the first sample, startFrame the frame of the downbeat; both count frames of the same context. */
export function cutBefore(samples, firstFrame, startFrame) {
  const drop = Math.max(0, Math.min(samples.length, Math.round(startFrame - firstFrame)));
  return drop ? samples.slice(drop) : samples;
}

/** The level of the latest chunk as 0..1 (peak), for the input meter. */
export function levelOf(chunk) {
  let max = 0;
  for (let i = 0; i < chunk.length; i++) max = Math.max(max, Math.abs(chunk[i]));
  return Math.min(1, max);
}

/** File name of a take: recording-<number>.wav. */
export const takeFileName = (n) => `recording-${String(n).padStart(2, '0')}.wav`;

/** The index of the first sample that reaches the fraction of the loudest sample (the start of a click), or -1 when nothing but silence was heard. */
export function findOnset(samples, fraction = 0.5, minimumPeak = 0.02) {
  let max = 0;
  for (let i = 0; i < samples.length; i++) max = Math.max(max, Math.abs(samples[i]));
  if (max < minimumPeak) return -1;
  const limit = max * fraction;
  for (let i = 0; i < samples.length; i++) if (Math.abs(samples[i]) >= limit) return i;
  return -1;
}
