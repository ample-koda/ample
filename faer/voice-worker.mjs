import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import readline from 'node:readline';

/** Runs one existing AMPLE voice-clone plugin and its Python child in a Linux process group. */
export function createVoiceWorker({ node, plugin, modelRoot, pythonRoot, assets }) {
  let child = null;
  async function stop() {
    if (!child?.pid) return;
    const running = child;
    const exited = running.exitCode === null && running.signalCode === null ? new Promise(resolve => running.once('exit', resolve)) : Promise.resolve();
    try { process.kill(-running.pid, 'SIGTERM'); } catch (error) { if (error.code !== 'ESRCH') throw error; }
    await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 5000))]);
    try { process.kill(-running.pid, 'SIGKILL'); } catch (error) { if (error.code !== 'ESRCH') throw error; }
    await exited;
    if (child === running) child = null;
  }
  return {
    key: 'voice-clone',
    unload: stop,
    async run(request, signal, context) {
      const render = request.render;
      if (request.capability !== 'voice-clone' || render?.block?.type !== 'voice-clone' || render.inputRoles?.length !== 1 || render.inputRoles[0] !== 'reference' || request.inputs.length !== 1) throw new Error('invalid voice-clone request');
      const model = String(render.block.parameters?.model || 'chatterbox').replace(/^"|"$/g, '');
      if (!['xtts', 'qwen3', 'chatterbox'].includes(model)) throw new Error('unsupported voice-clone model');
      const python = path.join(pythonRoot, `ample-faer-voice-${model}`, 'bin', 'python');
      const inputDir = path.join(context.jobDir, 'in');
      await fs.mkdir(inputDir);
      await fs.copyFile(path.join(assets, request.inputs[0]), path.join(inputDir, 'reference.bin'));
      const params = {
        jobDir: context.jobDir,
        block: { type: 'voice-clone', name: 'Faer block', parameters: render.block.parameters || {} },
        seed: String(render.seed), sampleRate: render.sampleRate, samplesPerBar: render.samplesPerBar,
        firstBar: render.firstBar, barCount: render.barCount, activeBars: render.activeBars || [],
        inputs: { reference: 'in/reference.bin' },
        settings: { modelRoot, xttsLicenceAccepted: process.env.AMPLE_XTTS_LICENCE_ACCEPTED === '1', pythonXtts: path.join(pythonRoot, 'ample-faer-voice-xtts', 'bin', 'python'), pythonQwen3: path.join(pythonRoot, 'ample-faer-voice-qwen3', 'bin', 'python'), pythonChatterbox: path.join(pythonRoot, 'ample-faer-voice-chatterbox', 'bin', 'python'), ffmpeg: 'ffmpeg', keepWarmS: 0, timeoutS: 1800 },
      };
      context.report('voice', null);
      child = spawn(node, [plugin], { cwd: context.jobDir, detached: true, stdio: ['pipe', 'pipe', 'pipe'] });
      const running = child;
      let errorText = '';
      running.stderr.on('data', chunk => { errorText = (errorText + chunk.toString()).slice(-8192); });
      const lines = readline.createInterface({ input: running.stdout });
      const aborted = () => { void stop(); };
      signal.addEventListener('abort', aborted, { once: true });
      try {
        const response = new Promise((resolve, reject) => {
          running.once('error', () => reject(new Error('voice plugin could not start')));
          running.once('exit', () => reject(new Error('voice plugin exited before completion')));
          lines.on('line', line => {
            let message;
            try { message = JSON.parse(line); } catch { return; }
            if (message.method === 'progress') context.report('voice', typeof message.params?.fraction === 'number' ? Math.max(0, Math.min(1, message.params.fraction)) : null);
            if (message.id === 1) message.error ? reject(Object.assign(new Error('voice synthesis failed'), /CUDA out of memory|CUDA error|CUBLAS_STATUS_ALLOC_FAILED|GPU.*unavailable/i.test(String(message.error?.message || message.error)) ? { code: 'FAER_GPU_OOM' } : {})) : resolve(message.result);
          });
        });
        running.stdin.end(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'render', params }) + '\n');
        const result = await response.catch(error => {
          if (/CUDA out of memory|CUDA error|CUBLAS_STATUS_ALLOC_FAILED/i.test(errorText)) throw Object.assign(new Error('Faer voice GPU unavailable'), { code: 'FAER_GPU_OOM' });
          throw error;
        });
        if (signal.aborted) throw signal.reason;
        if (!Array.isArray(result?.outputs) || result.outputs.length !== 1 || result.outputs[0]?.role !== 'audio') throw new Error('voice plugin returned an invalid output');
        const file = path.resolve(context.jobDir, result.outputs[0].path);
        const stored = await context.storeArtifact(file);
        const recipeFile = path.join(context.jobDir, 'render-recipe.json');
        await fs.writeFile(recipeFile, JSON.stringify(result.meta || {}));
        return { outputs: [{ role: 'audio', ...stored }, { role: 'render-recipe', ...await context.storeArtifact(recipeFile) }] };
      } finally {
        signal.removeEventListener('abort', aborted);
        await stop();
      }
    },
  };
}
