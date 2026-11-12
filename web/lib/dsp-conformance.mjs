// Conformance of the browser implementations against the server's renders (local-preview chunk 6.5). Pure and node-safe: reads web/lib/dsp-vectors.json (made by tools/gen-dsp-vectors.mjs from the
// real engine), renders the same input through the offline JS port, and measures the distance. The live graph is measured the same way in the browser (spec-tests/parity/dsp-conformance.spec.ts).
import { readFileSync } from 'node:fs';
import { PARITY_REFERENCES, testSignal, CONFORMANCE_SECONDS } from './dsp-references.mjs';

/** The vectors file: { cases: { type: { params, tempo, sampleRate, frames, channels: [base64 Int16] } } }. */
export function loadVectors(file = new URL('./dsp-vectors.json', import.meta.url)) { return JSON.parse(readFileSync(file, 'utf8')); }

/** The server's output of one case as Float32 channels (16-bit values / 32768). */
export function serverChannels(entry) {
  return entry.channels.map((text) => { const bytes = Buffer.from(text, 'base64'); return Float32Array.from(new Int16Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 2), (v) => v / 32768); });
}

/** The test signal as the server reads it: 16-bit quantized. */
export function quantizedInput() {
  const signal = testSignal(CONFORMANCE_SECONDS);
  return { sampleRate: signal.sampleRate, channels: signal.channels.map((channel) => Float32Array.from(channel, (v) => Math.max(-32768, Math.min(32767, Math.round(v * 32768))) / 32768)) };
}

/** Distance of a candidate from the server in dB below the server's energy (more negative is closer), skipping the first `skip` seconds (knob smoothing, filters settling). */
export function deviationDb(server, candidate, rate, skip = 0.05) {
  let difference = 0, energy = 0;
  const first = Math.floor(rate * skip);
  for (let c = 0; c < server.length; c++) {
    const a = candidate[Math.min(c, candidate.length - 1)], b = server[c];
    for (let i = first; i < b.length; i++) { const d = (a[i] ?? 0) - b[i]; difference += d * d; energy += b[i] * b[i]; }
  }
  return energy > 0 ? 10 * Math.log10(Math.max(difference, 1e-20) / energy) : NaN;
}

/** Offline JS port against the server for every vector: { type: deviationDb }. */
export async function offlineDeviations(vectors = loadVectors()) {
  const out = {};
  for (const [type, entry] of Object.entries(vectors.cases)) {
    const reference = PARITY_REFERENCES[type];
    if (!reference) continue;
    const input = quantizedInput();
    const result = await reference({ sampleRate: input.sampleRate, channels: input.channels.map((c) => c.slice()) }, entry.params, entry.tempo);
    out[type] = deviationDb(serverChannels(entry), result.channels, entry.sampleRate);
  }
  return out;
}

/**
 * How far each implementation may be from the server, in dB (the measured distance 2026-11-12 plus about 3 dB). A test fails when a port gets worse than this; when one gets better, lower the limit.
 * offline: the JS port of "Build and play preview"; live: the Web Audio graph or worklet of the live chain.
 */
export const OFFLINE_LIMITS_DB = {
  reverb: -70, gate: -60, limiter: -25, deesser: -50, eq: -55, peq: -80, filter: -55, compressor: -28, delay: -75, distortion: -70, chorus: -17, flanger: -17, phaser: -60, pan: -75, width: -75, volume: -70, tremolo: -75,
};
