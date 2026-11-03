import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';

export async function localOptions(options) {
  if (!options || typeof options !== 'object' || Array.isArray(options)) throw new Error('Choose the local Python environment and preinstalled separation model in plugin options.');
  if (typeof options.pythonPath !== 'string' || !path.isAbsolute(options.pythonPath)) throw new Error('pythonPath must be an absolute path to the local Python executable.');
  if (typeof options.modelDirectory !== 'string' || !path.isAbsolute(options.modelDirectory)) throw new Error('modelDirectory must be an absolute path to the local model folder.');
  if (typeof options.separatorModel !== 'string' || path.basename(options.separatorModel) !== options.separatorModel || !/^[A-Za-z0-9._-]{1,120}$/.test(options.separatorModel)) throw new Error('separatorModel must be a model filename in modelDirectory.');
  if (!fs.existsSync(options.pythonPath) || !fs.statSync(options.pythonPath).isFile()) throw new Error('The configured local Python executable is unavailable.');
  if (!fs.existsSync(options.modelDirectory) || !fs.statSync(options.modelDirectory).isDirectory()) throw new Error('The configured local model folder is unavailable.');
  const modelPath = path.join(options.modelDirectory, options.separatorModel);
  if (!fs.existsSync(modelPath) || !fs.statSync(modelPath).isFile()) throw new Error('The selected separation model is not installed; this plugin never downloads models.');
  if (options.modelLicense !== undefined && (typeof options.modelLicense !== 'string' || options.modelLicense.length > 128)) throw new Error('modelLicense must be a short license name or URL.');
  const drumMode = options.drumMode ?? 'skip';
  if (drumMode !== 'skip' && drumMode !== 'pitched') throw new Error('drumMode must be "skip" or "pitched".');
  const computeDevice = options.computeDevice ?? 'gpu';
  if (computeDevice !== 'gpu' && computeDevice !== 'cpu') throw new Error('computeDevice must be "gpu" or "cpu".');
  const hash = createHash('sha256');
  for await (const chunk of fs.createReadStream(modelPath)) hash.update(chunk);
  return { pythonPath: path.resolve(options.pythonPath), modelDirectory: path.resolve(options.modelDirectory), separatorModel: options.separatorModel, modelHash: hash.digest('hex'), modelLicense: options.modelLicense || 'not supplied', drumMode, computeDevice };
}

export function midiArtifactOutputs(artifacts) {
  const labels = new Set(['backing', 'combined', 'drums', 'bass', 'guitar', 'keys', 'strings', 'synth']);
  if (!Array.isArray(artifacts) || artifacts.length > 8) throw new Error('The local model returned an invalid number of backing MIDI tracks.');
  return artifacts.map((artifact, index) => {
    const label = artifact?.label;
    const file = artifact?.path;
    if (typeof label !== 'string' || (!labels.has(label) && !/^stem-[0-9]{2}$/.test(label)) || typeof file !== 'string' || !/^out\/[A-Za-z0-9._-]+\.mid$/.test(file) || file.split('/').some((part) => part === '.' || part === '..')) throw new Error('The local model returned an invalid backing MIDI artifact.');
    const role = label === 'combined' ? 'backing-midi-combined' : artifacts.length === 1 && label === 'backing' ? 'backing-midi' : `backing-midi-${label}-${String(index + 1).padStart(2, '0')}`;
    return { role, path: file, label };
  });
}

export function runLocalWorker(pythonPath, workerPath, jobDir, options, context) {
  const input = path.join(jobDir, 'in', 'audio.wav');
  const output = path.join(jobDir, 'out');
  fs.mkdirSync(output, { recursive: true });
  const child = spawn(pythonPath, ['-I', workerPath, input, output, JSON.stringify(options)], {
    cwd: jobDir,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'ignore'],
    env: { PATH: process.env.PATH || '', SystemRoot: process.env.SystemRoot || '', TEMP: process.env.TEMP || '', TMP: process.env.TMP || '', ...(options.computeDevice === 'cpu' ? { CUDA_VISIBLE_DEVICES: '' } : {}), HF_HUB_OFFLINE: '1', TRANSFORMERS_OFFLINE: '1' },
  });
  return new Promise((resolve, reject) => {
    let stdout = '', line = '';
    let settled = false;
    const finish = (error, value) => { if (settled) return; settled = true; clearInterval(cancel); error ? reject(error) : resolve(value); };
    const cancel = setInterval(() => { if (context.isCancelled()) child.kill(); }, 200);
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk) => {
      stdout += chunk;
      if (stdout.length > 256 * 1024) { child.kill(); finish(new Error('Local model produced too much status output.')); return; }
      line += chunk;
      const lines = line.split(/\r?\n/); line = lines.pop() || '';
      for (const item of lines) {
        if (!item.startsWith('AMPLE_PROGRESS ')) continue;
        try { const event = JSON.parse(item.slice(15)); context.progress(event.fraction, event.step, event.stepFraction); } catch { /* Ignore malformed progress; result validation remains authoritative. */ }
      }
    });
    child.once('error', () => finish(new Error('Could not start the configured local Python runtime.')));
    child.once('close', (code) => {
      if (context.isCancelled()) { finish(new Error('Local transcription was cancelled.')); return; }
      if (code !== 0) { finish(new Error('Local analysis failed. Check that audio-separator, its model files, soundfile, scipy and basic-pitch are installed in the selected Python environment.')); return; }
      try {
        const resultPath = path.join(output, 'result.json');
        const result = JSON.parse(fs.readFileSync(resultPath, 'utf8'));
        finish(null, result);
      } catch { finish(new Error('The local analysis did not return a valid bounded result.')); }
    });
  });
}
