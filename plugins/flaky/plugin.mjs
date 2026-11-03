// A plugin that misbehaves on purpose. The block parameter "voice" selects the behaviour.
import fs from 'node:fs';
import path from 'node:path';
import { serve, wav } from '../sdk/ample-plugin.mjs';

const sleep = ms => new Promise(r => setTimeout(r, ms));

serve({
  initialize: () => ({ protocol: 1, id: 'flaky', version: '1.0.0', blocks: ['ai-voice'] }),
  describe: () => ({ toolprint: { plugin: 'flaky' }, determinism: 'unverified' }),
  validate: () => ({ ok: true, messages: [] }),
  render: async (params, ctx) => {
    const mode = (params.block.parameters || {}).voice;
    const out = (name, bytes) => { fs.mkdirSync(path.join(params.jobDir, 'out'), { recursive: true }); fs.writeFileSync(path.join(params.jobDir, 'out', name), bytes); return 'out/' + name; };
    if (mode === 'crash') { process.exit(3); }
    if (mode === 'hang') { await new Promise(() => {}); }
    if (mode === 'garbage') { process.stdout.write('this is not json at all\n'); await sleep(5000); }
    if (mode === 'huge') { process.stdout.write(JSON.stringify({ jsonrpc: '2.0', method: 'log', params: { message: 'x'.repeat(2 * 1024 * 1024) } }) + '\n'); await sleep(5000); }
    if (mode === 'escape') { return { outputs: [{ role: 'vocal', path: '../../escape.bin' }] }; }
    if (mode === 'error') { throw new Error('the model is not installed'); }
    if (mode === 'logsecret') { process.stderr.write('token=' + process.env.TEST_TOKEN + '\n'); ctx.log('warn', 'using token ' + process.env.TEST_TOKEN); }
    const start = Date.now();
    if (mode === 'slow') { await sleep(400); }
    const samples = new Int16Array(Math.min(params.barCount * params.samplesPerBar, 100000));
    return { outputs: [{ role: 'vocal', path: out('vocal.wav', wav(samples, params.sampleRate)) }], meta: { start, end: Date.now() } };
  },
});
