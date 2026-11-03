import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import readline from 'node:readline';

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const rolePattern = /^[a-z][a-z0-9-]{0,39}$/;
const stageNames = { plan: 'planning', synthesis: 'synthesis', isolation: 'isolation' };
const LOW_VRAM_MIB = 20 * 1024;

export function aceServerMemoryFlags(vramMiB) {
  return !Number.isFinite(vramMiB) || vramMiB < LOW_VRAM_MIB ? ['--vae-chunk', '512'] : [];
}

async function gpuMemoryMiB() {
  return new Promise(resolve => {
    const child = spawn('nvidia-smi', ['--id=0', '--query-gpu=memory.total', '--format=csv,noheader,nounits'], { stdio: ['ignore', 'pipe', 'ignore'] });
    let output = '';
    let settled = false;
    const finish = value => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(value);
    };
    const timer = setTimeout(() => { child.kill(); finish(null); }, 3000);
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => { output = (output + chunk).slice(0, 128); });
    child.once('error', () => finish(null));
    child.once('close', code => finish(code === 0 && /^\s*\d+\s*$/.test(output) ? Number(output.trim()) : null));
  });
}

async function stop(child) {
  if (!child || !child.pid || child.exitCode !== null || child.signalCode !== null) return;
  const exited = new Promise(resolve => child.once('exit', resolve));
  child.kill('SIGTERM');
  await Promise.race([exited, pause(5000)]);
  if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
  await exited;
}

/** Owns one ACE server process and one plugin render at a time; unload waits for process exit. */
export function createAceWorker({ bin, models, plugin, node, assets }) {
  let ace = null;
  let pluginChild = null;
  let aceStderr = '';
  const port = 3815;
  const endpoint = `http://127.0.0.1:${port}`;

  async function ensureAce(signal) {
    if (ace && ace.exitCode === null && ace.signalCode === null) return;
    const memoryFlags = aceServerMemoryFlags(await gpuMemoryMiB());
    ace = spawn(bin, ['--models', models, '--host', '127.0.0.1', '--port', String(port), '--device', 'CUDA0', ...memoryFlags], { stdio: ['ignore', 'ignore', 'pipe'] });
    let startError = null;
    aceStderr = '';
    ace.stderr.setEncoding('utf8');
    ace.stderr.on('data', chunk => { aceStderr = (aceStderr + chunk).slice(-8192); });
    ace.on('error', error => { startError = error; });
    for (let i = 0; i < 120; i++) {
      if (signal.aborted) throw signal.reason;
      if (startError || ace.exitCode !== null || ace.signalCode !== null) throw Object.assign(new Error(`ACE server exited before it was ready: ${startError?.message || ''}; ${aceStderr.trim()}`), { code: 'FAER_GPU_UNAVAILABLE' });
      try {
        const response = await fetch(endpoint + '/health', { signal: AbortSignal.timeout(1000) });
        if (response.ok) return;
      } catch { /* ACE may still be loading. */ }
      await pause(500);
    }
    throw Object.assign(new Error(`ACE server did not become ready: ${aceStderr.trim()}`), { code: 'FAER_GPU_UNAVAILABLE' });
  }

  return {
    key: 'ace',
    async unload() {
      await stop(pluginChild);
      pluginChild = null;
      await stop(ace);
      ace = null;
    },
    async run(request, signal, context) {
      const expected = request.capability === 'song' ? 'ace-music' : request.capability === 'voice-ace' ? 'ai-voice' : null;
      const render = request.render;
      if (!expected || !render || render.block?.type !== expected || !Array.isArray(render.inputRoles) || render.inputRoles.length !== request.inputs.length || render.inputRoles.some(role => typeof role !== 'string' || !rolePattern.test(role))) throw new Error('invalid ACE render request');
      if (!Number.isSafeInteger(render.sampleRate) || render.sampleRate < 8000 || render.sampleRate > 192000 || !Number.isSafeInteger(render.samplesPerBar) || render.samplesPerBar < 1 || !Number.isSafeInteger(render.barCount) || render.barCount < 1 || render.barCount > 1024 || !/^\d+$/.test(String(render.seed))) throw new Error('invalid ACE render geometry');
      context.report('preparing', null);
      const inDir = path.join(context.jobDir, 'in');
      await fs.mkdir(inDir);
      const inputs = {};
      for (let i = 0; i < request.inputs.length; i++) {
        const role = render.inputRoles[i];
        if (inputs[role]) throw new Error('duplicate ACE input role');
        inputs[role] = `in/${role}.bin`;
        await fs.copyFile(path.join(assets, request.inputs[i]), path.join(inDir, `${role}.bin`));
      }
      await ensureAce(signal);
      const params = {
        jobDir: context.jobDir,
        block: { type: expected, name: 'Faer block', parameters: render.block.parameters || {} },
        seed: String(render.seed), sampleRate: render.sampleRate,
        samplesPerBar: render.samplesPerBar, firstBar: render.firstBar,
        barCount: render.barCount, activeBars: render.activeBars || [], inputs,
        settings: { ...render.settings, backend: 'server', endpoint, allowRemote: false, modelsFolder: models, aceBinFolder: path.dirname(bin), vocalIsolation: request.capability === 'voice-ace' ? 'ace' : 'off' },
      };
      context.report('planning', null);
      pluginChild = spawn(node, [plugin], { cwd: context.jobDir, stdio: ['pipe', 'pipe', 'pipe'] });
      const child = pluginChild;
      const lines = readline.createInterface({ input: child.stdout });
      let stderr = '';
      child.stderr.setEncoding('utf8');
      child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-8192); });
      const aborted = () => { void stop(child); };
      signal.addEventListener('abort', aborted, { once: true });
      try {
        const response = new Promise((resolve, reject) => {
          child.once('error', error => reject(Object.assign(new Error(`ACE plugin could not start: ${error.message}`), { code: 'FAER_ACE_FAILED' })));
          child.once('exit', code => reject(Object.assign(new Error(`ACE plugin exited (${code}): ${stderr.trim()}`), { code: 'FAER_ACE_FAILED' })));
          lines.on('line', line => {
            let message;
            try { message = JSON.parse(line); } catch { return; }
            if (message.method === 'progress') {
              const stage = stageNames[message.params?.step];
              if (stage) context.report(stage, typeof message.params?.fraction === 'number' ? Math.max(0, Math.min(1, message.params.fraction)) : null);
            }
            if (message.id === 1) message.error ? reject(Object.assign(new Error(`ACE render failed: ${typeof message.error?.message === 'string' ? message.error.message : JSON.stringify(message.error)}${stderr ? `; ${stderr.trim()}` : ''}`), { code: /CUDA out of memory|CUDA error|CUBLAS_STATUS_ALLOC_FAILED|GPU.*unavailable/i.test(String(message.error?.message || message.error)) ? 'FAER_GPU_OOM' : 'FAER_ACE_FAILED' })) : resolve(message.result);
          });
        });
        child.stdin.end(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'render', params }) + '\n');
        let result;
        try { result = await response; }
        catch (error) {
          if (ace?.exitCode !== null || ace?.signalCode !== null) throw Object.assign(new Error(`ACE server stopped during rendering: ${aceStderr.trim()} ${error.message}`), { code: 'FAER_GPU_UNAVAILABLE' });
          throw Object.assign(new Error(`${error.message}${aceStderr ? `; ACE server stderr: ${aceStderr.trim()}` : ''}`), { code: error.code || 'FAER_ACE_FAILED' });
        }
        if (signal.aborted) throw signal.reason;
        if (!Array.isArray(result?.outputs) || result.outputs.length !== 1) throw Object.assign(new Error('ACE returned an invalid output'), { code: 'FAER_ARTIFACT_INVALID' });
        const output = result.outputs[0];
        if (!rolePattern.test(output.role) || typeof output.path !== 'string') throw Object.assign(new Error('ACE returned an invalid output path'), { code: 'FAER_ARTIFACT_INVALID' });
        const file = path.resolve(context.jobDir, output.path);
        let stored;
        try { stored = await context.storeArtifact(file); }
        catch (error) { throw Object.assign(new Error(`ACE output could not be stored: ${error.message}`), { code: 'FAER_ARTIFACT_INVALID' }); }
        const recipeFile = path.join(context.jobDir, 'render-recipe.json');
        await fs.writeFile(recipeFile, JSON.stringify(result.meta || {}));
        return { outputs: [{ role: output.role, ...stored }, { role: 'render-recipe', ...await context.storeArtifact(recipeFile) }] };
      } finally {
        signal.removeEventListener('abort', aborted);
        await stop(child);
        if (pluginChild === child) pluginChild = null;
      }
    },
  };
}
