// Conformance vectors (local-preview chunk 6.5): renders the deterministic test signal through each block with the real server engine (the CLI, no server process) and stores the result in
// web/lib/dsp-vectors.json. The offline JS port and the live graph are then checked against it (web/lib/test/dsp-conformance.test.mjs, spec-tests/parity/dsp-conformance.spec.ts), and
// tools/dsp-deviations.mjs lists the distance per block. The three implementations stay separate on purpose (owner decision, 2026-11-12); this is how they are kept in sync.
//   node tools/gen-dsp-vectors.mjs [type ...]     regenerate all (or some) vectors; needs a built CLI (AMPLE_CLI_DLL or src/Ample.Cli/bin/Debug/net9.0/Ample.Cli.dll)
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { testSignal, CONFORMANCE_PARAMS, CONFORMANCE_SECONDS, CONFORMANCE_TEMPO } from '../web/lib/dsp-references.mjs';

const root = fileURLToPath(new URL('..', import.meta.url)), target = root + 'web/lib/dsp-vectors.json';

/** The signal as the 16-bit values the server reads (also what the JS side is checked on, so the input is identical bit for bit). */
export function quantizedSignal() {
  const signal = testSignal(CONFORMANCE_SECONDS);
  const ints = signal.channels.map((channel) => Int16Array.from(channel, (v) => Math.max(-32768, Math.min(32767, Math.round(v * 32768)))));
  return { sampleRate: signal.sampleRate, ints, channels: ints.map((c) => Float32Array.from(c, (v) => v / 32768)) };
}

const wavOf = (signal) => {
  const frames = signal.ints[0].length, bytes = Buffer.alloc(44 + frames * 4);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(2, 22);
  bytes.writeUInt32LE(signal.sampleRate, 24); bytes.writeUInt32LE(signal.sampleRate * 4, 28); bytes.writeUInt16LE(4, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 4, 40);
  for (let i = 0; i < frames; i++) { bytes.writeInt16LE(signal.ints[0][i], 44 + i * 4); bytes.writeInt16LE(signal.ints[1][i], 46 + i * 4); }
  return bytes;
};

/** Source blocks (no input): the built-in drums, rendered by the server for two bars. The hits of web/lib/drum-core.mjs must add up to exactly these samples. */
export const DRUM_CASES = {
  'beat-A': { pattern: 'beat-A', song: '', bars: 2 },
  'beat-B': { pattern: 'beat-B', song: '', bars: 2 },
  'song': { pattern: 'beat-A', song: 'beat-A beat-B beat-B', bars: 3 },
};

/** Reads a PCM or float WAV into Int16 channels (the first `frames` frames). */
export function readWav(buffer, frames) {
  let at = 12, format = null, data = null;
  while (at + 8 <= buffer.length) {
    const id = buffer.toString('ascii', at, at + 4), size = buffer.readUInt32LE(at + 4);
    if (id === 'fmt ') format = { tag: buffer.readUInt16LE(at + 8), channels: buffer.readUInt16LE(at + 10), rate: buffer.readUInt32LE(at + 12), bits: buffer.readUInt16LE(at + 22) };
    if (id === 'data') { data = buffer.subarray(at + 8, at + 8 + size); break; }
    at += 8 + size + (size % 2);
  }
  if (!format || !data) throw new Error('not a WAV');
  const bytes = format.bits / 8, total = Math.floor(data.length / bytes / format.channels), count = Math.min(frames, total);
  const channels = Array.from({ length: format.channels }, () => new Int16Array(count));
  for (let i = 0; i < count; i++) for (let c = 0; c < format.channels; c++) {
    const offset = (i * format.channels + c) * bytes;
    channels[c][i] = format.tag === 3 && bytes === 4 ? Math.round(Math.max(-1, Math.min(1, data.readFloatLE(offset))) * 32767) : bytes === 2 ? data.readInt16LE(offset) : bytes === 3 ? data.readIntLE(offset, 3) >> 8 : data.readInt32LE(offset) >> 16;
  }
  return { sampleRate: format.rate, channels };
}

function render(cli, type, params, signal, work) {
  const lines = Object.entries(params).map(([k, v]) => `  ${k} ${v}`).join('\n');
  mkdirSync(join(work, 'recordings'), { recursive: true });
  writeFileSync(join(work, 'recordings', 'in.wav'), wavOf(signal));
  writeFileSync(join(work, 'vec.amplet'), `composition "vec" { ample 1  tempo ${CONFORMANCE_TEMPO}  master-seed 17 }\n\nimport-audio src at 1 for 1 {\n  file recordings/in.wav\n}\n\n${type} fx {\n${lines}\n  from src.audio -> audio\n}\n\nmaster master {\n  from fx.audio -> audio\n}\n`);
  const out = join(work, 'out.wav');
  cli(['build', 'vec.amplet', '--block', 'fx', '--out', out, '--root', root], work);
  return readWav(readFileSync(out), signal.ints[0].length);
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  const dll = process.env.AMPLE_CLI_DLL || root + 'src/Ample.Cli/bin/Debug/net9.0/Ample.Cli.dll';
  if (!existsSync(dll)) { console.error(`CLI not found: ${dll} (dotnet build src/Ample.Cli, or set AMPLE_CLI_DLL)`); process.exit(1); }
  const cli = (args, cwd) => execFileSync('dotnet', [dll, ...args], { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  const only = process.argv.slice(2), signal = quantizedSignal();
  const existing = existsSync(target) ? JSON.parse(readFileSync(target, 'utf8')) : { cases: {} };
  const base = mkdtempSync(join(tmpdir(), 'ample-vec-'));
  try {
    cli(['new', 'p'], base);
    const work = join(base, 'p');
    for (const [type, params] of Object.entries(CONFORMANCE_PARAMS)) {
      if (only.length && !only.includes(type)) continue;
      try {
        const out = render(cli, type.split('~')[0], params, signal, work);
        existing.cases[type] = { params, tempo: CONFORMANCE_TEMPO, sampleRate: out.sampleRate, frames: out.channels[0].length, channels: out.channels.map((c) => Buffer.from(c.buffer, c.byteOffset, c.byteLength).toString('base64')) };
        console.log(`${type}: ${out.channels.length} channel(s), ${out.channels[0].length} frames`);
      } catch (error) { console.error(`${type}: FAILED ${String(error.stderr || error.message).split('\n')[0]}`); }
    }
  } finally { rmSync(base, { recursive: true, force: true }); }
  if (!only.length || only.includes('drums')) {
    const base2 = mkdtempSync(join(tmpdir(), 'ample-vec-'));
    try {
      cli(['new', 'p'], base2);
      const work = join(base2, 'p');
      existing.sources ||= {};
      for (const [name, c] of Object.entries(DRUM_CASES)) {
        const song = `composition "vec" { ample 1  tempo ${CONFORMANCE_TEMPO}  master-seed 17 }

drums fx {
  kit builtin
  pattern ${c.pattern}
  bars ${c.bars}
${c.song ? `  song "${c.song}"
` : ''}}

master master {
  from fx.audio -> audio
}
`;
        writeFileSync(join(work, 'drums.amplet'), song);
        const out = join(work, 'drums.wav');
        cli(['build', 'drums.amplet', '--block', 'fx', '--out', out, '--root', root], work);
        const seed = /seed of fx: ([0-9]+)/.exec(((r) => r.stdout + r.stderr)(spawnSync('dotnet', [dll, 'seed', 'drums.amplet', 'fx'], { cwd: work, encoding: 'utf8' })))?.[1];
        const wav = readWav(readFileSync(out), 1e9);
        existing.sources['drums:' + name] = { params: { tempo: CONFORMANCE_TEMPO, pattern: c.pattern, song: c.song }, bars: c.bars, seed, sampleRate: wav.sampleRate, frames: wav.channels[0].length, channels: wav.channels.map((ch) => Buffer.from(ch.buffer, ch.byteOffset, ch.byteLength).toString('base64')) };
        console.log(`drums:${name}: ${wav.channels.length} channel(s), ${wav.channels[0].length} frames, seed ${seed}`);
      }
    } finally { rmSync(base2, { recursive: true, force: true }); }
  }
  existing.signal = { seconds: CONFORMANCE_SECONDS, note: 'testSignal() of web/lib/dsp-references.mjs, quantized to 16 bit (the server input)' };
  existing.generator = 'node tools/gen-dsp-vectors.mjs';
  writeFileSync(target, JSON.stringify(existing));
  console.log(`wrote ${target}`);
}
