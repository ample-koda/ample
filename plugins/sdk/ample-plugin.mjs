// Minimal helper for AMPLE plugins written in Node (protocol v1: JSON-RPC 2.0, one JSON message per line on stdin/stdout).
// Usage: import { serve } from '../sdk/ample-plugin.mjs'; serve({ initialize, describe, validate, render });
// A handler receives (params, ctx); ctx.progress(fraction, step, stepFraction): fraction is the whole block from 0 to 1 (or null), step names a sub-step and stepFraction is its own 0 to 1 (null: busy, end unknown), ctx.log(level, message), ctx.isCancelled(). Return the result object.
import readline from 'node:readline';

export function serve(handlers) {
  const rl = readline.createInterface({ input: process.stdin });
  const send = o => process.stdout.write(JSON.stringify(o) + '\n');
  const cancelled = new Set();
  rl.on('line', async line => {
    let msg;
    try { msg = JSON.parse(line); } catch { return; }
    if (msg.method === 'shutdown') { process.exit(0); }
    if (msg.method === 'cancel') { cancelled.add(msg.params && msg.params.id); return; }
    if (msg.id === undefined) { return; }
    const ctx = {
      progress: (fraction, step, stepFraction) => send({ jsonrpc: '2.0', method: 'progress', params: { id: msg.id, fraction, step, stepFraction } }),
      log: (level, message) => send({ jsonrpc: '2.0', method: 'log', params: { level, message } }),
      isCancelled: () => cancelled.has(msg.id),
    };
    try {
      const fn = handlers[msg.method];
      if (!fn) { throw Object.assign(new Error('unknown method ' + msg.method), { code: -32601 }); }
      send({ jsonrpc: '2.0', id: msg.id, result: await fn(msg.params || {}, ctx) });
    } catch (e) {
      send({ jsonrpc: '2.0', id: msg.id, error: { code: e.code || -32000, message: String(e.message) } });
    }
  });
}

/** Encodes 16-bit mono PCM samples (Int16Array) as a WAV buffer. */
export function wav(samples, rate) {
  const buf = Buffer.alloc(44 + samples.length * 2);
  buf.write('RIFF', 0); buf.writeInt32LE(36 + samples.length * 2, 4); buf.write('WAVEfmt ', 8);
  buf.writeInt32LE(16, 16); buf.writeInt16LE(1, 20); buf.writeInt16LE(1, 22); buf.writeInt32LE(rate, 24);
  buf.writeInt32LE(rate * 2, 28); buf.writeInt16LE(2, 32); buf.writeInt16LE(16, 34); buf.write('data', 36); buf.writeInt32LE(samples.length * 2, 40);
  for (let i = 0; i < samples.length; i++) { buf.writeInt16LE(samples[i], 44 + i * 2); }
  return buf;
}
