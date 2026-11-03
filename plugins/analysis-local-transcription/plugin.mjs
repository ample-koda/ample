import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { serve } from '../sdk/ample-plugin.mjs';
import { localOptions, midiArtifactOutputs, runLocalWorker } from './runtime.mjs';

const worker = fileURLToPath(new URL('./worker.py', import.meta.url));

serve({
  initialize: () => ({ protocol: 1, id: 'analysis-local-transcription', version: '0.1.0', blocks: [] }),
  describe: () => ({ toolprint: { plugin: 'analysis-local-transcription' }, determinism: 'unverified' }),
  analyze: async (params, context) => {
    if (params.analysis !== 'transcription') throw new Error('This plugin only offers transcription.');
    const options = await localOptions(params.options);
    const input = path.resolve(params.jobDir, params.input);
    if (!input.startsWith(path.resolve(params.jobDir) + path.sep) || !fs.existsSync(input)) throw new Error('The transcription input is not in the private analysis job.');
    context.progress(0.05, 'local-models', 0);
    const result = await runLocalWorker(options.pythonPath, worker, params.jobDir, options, context);
    const midiOutputs = midiArtifactOutputs(result.analysis.midiArtifacts);
    const { midiArtifacts, ...analysis } = result.analysis;
    return {
      result: { ...analysis, midiArtifacts: midiOutputs.map(({ role, label }) => ({ role, label })) },
      outputs: [
        { role: 'vocals', path: 'out/vocals.wav' },
        { role: 'backing', path: 'out/backing.wav' },
        { role: 'vocal-midi', path: 'out/vocal-midi.mid' },
        ...midiOutputs.map(({ role, path: outputPath }) => ({ role, path: outputPath })),
      ],
    };
  },
});
