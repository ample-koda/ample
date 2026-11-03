import fs from 'node:fs';
import crypto from 'node:crypto';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { serve } from '../sdk/ample-plugin.mjs';

const parameter = (p, name, fallback = '') => {
  const value = String(p[name] ?? fallback).trim();
  return value.startsWith('"') ? value.slice(1, value.endsWith('"') ? -1 : undefined).replace(/\\(.)/gs, (_, char) => char === 'n' ? '\n' : char === 't' ? '\t' : char).trim() : value.replaceAll('\\n', '\n').trim();
};
const verifiedDependencies = new Set();
const workerPath = path.join(path.dirname(fileURLToPath(import.meta.url)), 'worker.py');
// Warm workers (voice-clone.keepWarmS > 0): one long-lived Python worker per interpreter keeps the loaded model in memory so a rebuild
// shortly after skips the model load. The worker drops it when idle too long or when video memory gets tight; the plugin only relays
// its own cache events to the log (event, reason, counts and MB; no process information of any kind).
const warm = new Map();
let lastLog = null;
const logCache = (message) => { try { lastLog?.('info', message); } catch { /* the log is best effort */ } };
const cacheMessage = (v) => `voice model cache: ${v.event}${v.model ? ` ${v.model}` : ''}${v.reason ? ` (${v.reason})` : ''}${typeof v.freeMb === 'number' ? `, free video memory ${v.freeMb} MB` : ''}`;
function warmWorker(python, dependencyKey) {
  const key = path.resolve(python);
  const existing = warm.get(key);
  if (existing && existing.child.exitCode === null && !existing.child.killed) { existing.dependencyKey = dependencyKey; return existing; }
  const child = spawn(python, [workerPath, '--serve'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  const w = { child, current: null, buffer: '', stderr: '', dependencyKey };
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', data => { w.stderr = (w.stderr + data).slice(-8192); });
  child.stdout.on('data', data => {
    w.buffer += data.toString();
    const lines = w.buffer.split(/\r?\n/);
    w.buffer = lines.pop();
    for (const line of lines) {
      try {
        if (line === 'AMPLE_DEPENDENCIES_OK') verifiedDependencies.add(w.dependencyKey);
        else if (line.startsWith('AMPLE_PROGRESS ')) { const v = JSON.parse(line.slice(15)); w.current?.ctx.progress(v.fraction, v.step, v.stepFraction ?? null); }
        else if (line.startsWith('AMPLE_CACHE ')) logCache(cacheMessage(JSON.parse(line.slice(12))));
        else if (line.startsWith('AMPLE_RESULT ')) { const r = JSON.parse(line.slice(13)); const cur = w.current; w.current = null; if (cur) r.ok ? cur.resolve() : cur.reject(new Error(`Voice synthesis failed: ${String(r.error || '').replace(/[A-Za-z]:\\[^\s]+/g, '[local path]').slice(0, 500)}`)); }
      } catch { /* a malformed line is ignored */ }
    }
  });
  const gone = (message) => { if (warm.get(key) === w) warm.delete(key); const cur = w.current; w.current = null; if (cur) cur.reject(new Error(message)); };
  child.on('error', () => gone('Could not start the configured Python interpreter. Check the voice-clone Python settings.'));
  // the last lines the worker printed to stderr say why it stopped (a missing package, out of memory); local paths are masked
  const why = () => { const t = w.stderr.split(/\r?\n/).map(x => x.trim()).filter(Boolean).slice(-4).join(' | ').replace(/[A-Za-z]:\\[^\s"']+/g, '[local path]').slice(-500); return t ? ` Last output: ${t}` : ''; };
  child.on('close', code => gone(`The voice worker stopped (exit ${code}).${why()}`));
  warm.set(key, w);
  return w;
}
function runWarm(python, payload, dependencyKey, ctx, timeoutS) {
  lastLog = ctx.log;
  const w = warmWorker(python, dependencyKey);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { w.child.kill(); verifiedDependencies.delete(dependencyKey); reject(new Error('Voice synthesis timed out.')); }, timeoutS * 1000);
    w.current = { ctx, resolve: () => { clearTimeout(timer); resolve(); }, reject: e => { clearTimeout(timer); reject(e); } };
    w.child.stdin.write(JSON.stringify(payload) + '\n');
  });
}
serve({
  initialize: () => ({ protocol: 1, id: 'voice-clone', version: '0.1.6', blocks: ['voice-clone'] }),
  describe: () => ({ toolprint: { plugin: 'voice-clone', adapter: '1' }, determinism: 'unverified' }),
  render: async (params, ctx) => {
    const p = params.block.parameters || {}, s = params.settings || {};
    const model = parameter(p, 'model', 'xtts'), text = parameter(p, 'text'), language = parameter(p, 'language', 'nl');
    const referenceText = parameter(p, 'reference_text'), auto = parameter(p, 'auto_detect') === 'on';
    const input = params.inputs?.reference;
    if (!input) throw new Error('Choose a voice sample from the project before building this block.');
    if (!text) throw new Error('Enter the words this block should speak.');
    if (model === 'qwen3' && !referenceText && !auto) throw new Error('Qwen3 needs the sample transcript; enter it or enable auto detect.');
    if (!['xtts', 'qwen3', 'chatterbox'].includes(model)) throw new Error('Choose XTTS, Qwen3-TTS or Chatterbox.');
    const inputPath = path.join(params.jobDir, input), outputPath = path.join(params.jobDir, 'spoken.wav');
    if (!fs.existsSync(inputPath)) throw new Error('The staged voice sample is missing.');
    const modelFolder = path.join(s.modelRoot || '', { xtts: 'XTTS-v2', qwen3: 'Qwen3-TTS-0.6B', chatterbox: 'Chatterbox-Multilingual' }[model]);
    const requiredFiles = model === 'xtts' ? ['model.pth', 'config.json'] : model === 'qwen3' ? ['model.safetensors', 'config.json'] : ['ve.pt'];
    const missingFiles = requiredFiles.filter(file => !fs.existsSync(path.join(modelFolder, file)));
    if (missingFiles.length) throw new Error(`The ${model} model is incomplete under the configured voice-clone.modelRoot; missing ${missingFiles.join(', ')}.`);
    const args = [path.join(path.dirname(fileURLToPath(import.meta.url)), 'worker.py')];
    const sharedPython = path.join(s.modelRoot || '', 'venv', process.platform === 'win32' ? 'Scripts/python.exe' : 'bin/python');
    // an interpreter set for this engine wins (pythonXtts / pythonQwen3 / pythonChatterbox), then voice-clone.python, then the shared venv for Chatterbox, then the generic default
    const enginePython = String({ xtts: s.pythonXtts, qwen3: s.pythonQwen3, chatterbox: s.pythonChatterbox }[model] || '').trim();
    const explicitPython = Boolean(enginePython) || Boolean(s.python && s.python !== 'python');
    const python = enginePython || (explicitPython ? s.python : model === 'chatterbox' && fs.existsSync(sharedPython) ? sharedPython : (s.python || 'python'));
    // which setting chose the interpreter, so a failing environment can be named in the error
    const engineSetting = `voice-clone.${{ xtts: 'pythonXtts', qwen3: 'pythonQwen3', chatterbox: 'pythonChatterbox' }[model]}`;
    const pythonSetting = enginePython ? engineSetting : explicitPython ? 'voice-clone.python' : model === 'chatterbox' && fs.existsSync(sharedPython) ? 'the shared voice-clone venv under the model root' : null;
    const dependencyKey = `${path.resolve(python)}|${model}|${auto}`;
    const dependenciesCached = verifiedDependencies.has(dependencyKey);
    const payload = { seed: String(params.seed), sample: inputPath, output: outputPath, model, text, language, referenceText, autoDetect: auto, modelRoot: s.modelRoot || '', ffmpeg: s.ffmpeg || 'ffmpeg', dependenciesCached, explicitPython, pythonSetting, engineSetting, keepWarmS: Number(s.keepWarmS) || 0, pressureMb: Number(s.pressureMb) || 1024, minFreeMb: Number(s.minFreeMb) || 1000 };
    if (payload.keepWarmS > 0) await runWarm(python, payload, dependencyKey, ctx, s.timeoutS || 1800);
    else await new Promise((resolve, reject) => {
      const child = spawn(python, args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
      let stderr = '';
      let stdoutBuffer = '';
      child.stderr.setEncoding('utf8');
      child.stderr.on('data', data => { stderr = (stderr + data).slice(-8192); });
      child.stdout.on('data', data => {
        stdoutBuffer += data.toString();
        const lines = stdoutBuffer.split(/\r?\n/);
        stdoutBuffer = lines.pop();
        for (const line of lines) {
          if (line === 'AMPLE_DEPENDENCIES_OK') verifiedDependencies.add(dependencyKey);
          else if (line.startsWith('AMPLE_PROGRESS ')) { const v = JSON.parse(line.slice(15)); ctx.progress(v.fraction, v.step, v.stepFraction ?? null); }
        }
      });
      child.stdin.end(JSON.stringify(payload));
      const timer = setTimeout(() => { child.kill(); verifiedDependencies.delete(dependencyKey); reject(new Error('Voice synthesis timed out.')); }, (s.timeoutS || 1800) * 1000);
      child.on('error', e => { clearTimeout(timer); verifiedDependencies.delete(dependencyKey); reject(new Error('Could not start the configured Python interpreter. Check voice-clone.python settings.')); });
      child.on('close', code => {
        clearTimeout(timer);
        if (code === 0) { resolve(); return; }
        verifiedDependencies.delete(dependencyKey);
        const detail = stderr.split(/\r?\n/).map(line => line.trim()).filter(line => line && line !== '```').at(-1)?.replace(/[A-Za-z]:\\[^\s]+/g, '[local path]').slice(0, 500);
        reject(new Error(`Voice synthesis failed (exit ${code})${detail ? `: ${detail}` : '. Check the Project log for its current stage and confirm the configured dependencies.'}`));
      });
    });
    if (!fs.existsSync(outputPath)) throw new Error('Voice synthesis completed without an audio file.');
    const recipe = fs.existsSync(outputPath + '.recipe.json') ? JSON.parse(fs.readFileSync(outputPath + '.recipe.json', 'utf8')) : {};
    return { outputs: [{ role: 'audio', path: outputPath }], meta: { recipe: { ...recipe, seed: String(params.seed), workerSha256: crypto.createHash('sha256').update(fs.readFileSync(workerPath)).digest('hex'), pluginSha256: crypto.createHash('sha256').update(fs.readFileSync(fileURLToPath(import.meta.url))).digest('hex') } } };
  },
});
