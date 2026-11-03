// Test analysis plugin: answers the analyze request with fixed, schema-valid (or on purpose invalid) results. No GPU, no network.
import fs from 'node:fs';
import path from 'node:path';
import { serve } from '../sdk/ample-plugin.mjs';

serve({
  initialize: () => ({ protocol: 1, id: 'analysis-fake', version: '1.0.0', blocks: [] }),
  describe: () => ({ toolprint: { plugin: 'analysis-fake' }, determinism: 'exact' }),
  analyze: async (params, ctx) => {
    const size = fs.statSync(path.join(params.jobDir, params.input)).size;
    ctx.log('info', 'analysing ' + size + ' bytes');
    if (params.analysis === 'transcription') {
      ctx.progress(0.5, 'separation', 0.5);
      const out = path.join(params.jobDir, 'out');
      const nested = params.options?.nested === true;
      const folder = nested ? path.join(out, 'stems') : out;
      fs.mkdirSync(folder, { recursive: true });
      const vocal = path.join(folder, 'vocals.wav');
      const backing = path.join(folder, 'backing.wav');
      fs.copyFileSync(path.join(params.jobDir, params.input), vocal);
      fs.copyFileSync(path.join(params.jobDir, params.input), backing);
      const outputs = [
        { role: 'vocals', path: nested ? 'out/stems/vocals.wav' : 'out/vocals.wav' },
        { role: 'backing', path: nested ? 'out/stems/backing.wav' : 'out/backing.wav' },
      ];
      if (params.options?.includeMidi === true) {
        fs.writeFileSync(path.join(folder, 'backing.mid'), Buffer.from([
          0x4d, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 0, 96,
          0x4d, 0x54, 0x72, 0x6b, 0, 0, 0, 12,
          0, 0x90, 60, 80, 96, 0x80, 60, 0, 0, 0xff, 0x2f, 0,
        ]));
        outputs.push({ role: 'backing-midi', path: nested ? 'out/stems/backing.mid' : 'out/backing.mid' });
      }
      return {
        result: { tool: 'analysis-fake', toolVersion: '1.0.0', model: 'generated-fixture', sampleRate: 22050, sourceSamples: 22050, warnings: [], vocalPitch: { hopSeconds: 0.01, voicedFrames: 1, frames: [{ t: 0, hz: 220, midi: 57, confidence: 0.99 }] } },
        outputs: params.options?.escape
          ? [{ role: 'vocals', path: '../outside.wav' }]
          : outputs,
      };
    }
    if (params.analysis === 'key') { return { result: { key: 'C major', tonic: 'C', mode: 'weird', confidence: 0.5, chroma: [], candidates: [] } }; }
    return { result: { bpm: 120, confidence: 0.9, beatSeconds: [0, 0.5, 1], firstBeatSeconds: 0, inputBytes: size } };
  },
});
