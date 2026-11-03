// The reference test plugin: deterministic, tiny, no GPU. Renders a sawtooth from the seed.
import fs from 'node:fs';
import path from 'node:path';
import { serve, wav } from '../sdk/ample-plugin.mjs';

serve({
  initialize: () => ({ protocol: 1, id: 'echo', version: '1.0.0', blocks: ['ai-voice'] }),
  describe: () => ({ toolprint: { plugin: 'echo', engine: 'echo-saw-1' }, determinism: 'exact' }),
  validate: params => ({ ok: true, messages: [] }),
  render: async (params, ctx) => {
    const rate = params.sampleRate;
    const n = Math.min(params.barCount * params.samplesPerBar, 20000000);
    const inc = 200 + Number(BigInt(params.seed) % 400n);
    const samples = new Int16Array(n);
    for (let i = 0; i < n; i++) { samples[i] = (Math.floor((i * inc * 65536) / rate) % 65536) - 32768; samples[i] = Math.trunc(samples[i] / 5); }
    ctx.progress(0.5);
    fs.mkdirSync(path.join(params.jobDir, 'out'), { recursive: true });
    fs.writeFileSync(path.join(params.jobDir, 'out', 'vocal.wav'), wav(samples, rate));
    fs.writeFileSync(path.join(params.jobDir, 'out', 'melody.json'), JSON.stringify({ seed: params.seed, notes: [60, 62, 64] }));
    ctx.log('info', 'rendered ' + n + ' samples');
    return {
      outputs: [{ role: 'vocal', path: 'out/vocal.wav' }, { role: 'melody', path: 'out/melody.json' }],
      meta: { envKeys: Object.keys(process.env), hasTestSecret: Object.hasOwn(process.env, 'AMPLE_TEST_SECRET'), token: process.env.TEST_TOKEN ?? null, inputs: Object.keys(params.inputs) },
    };
  },
});
