import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';

/** Runs a dedicated Demucs subprocess for one two-stem job; exit unloads its GPU memory. */
export function createSeparationWorker({ python, assets }) {
  let child = null;
  async function stop() {
    if (!child || !child.pid || child.exitCode !== null || child.signalCode !== null) return;
    const running = child;
    const exited = new Promise(resolve => running.once('exit', resolve));
    running.kill('SIGTERM');
    await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 5000))]);
    if (running.exitCode === null && running.signalCode === null) running.kill('SIGKILL');
    await exited;
  }
  return {
    key: 'demucs',
    unload: stop,
    async run(request, signal, context) {
      const { format, sampleRate, frames, channels } = request.render || {};
      if (request.capability !== 'separation' || request.inputs.length !== 1 || !['wav', 'flac'].includes(format) || !Number.isSafeInteger(sampleRate) || sampleRate < 8000 || sampleRate > 192000 || !Number.isSafeInteger(frames) || frames < 1 || frames > sampleRate * 600 || ![1, 2].includes(channels)) throw new Error('invalid separation request');
      const input = path.join(context.jobDir, `input.${format}`);
      await fs.copyFile(path.join(assets, request.inputs[0]), input);
      context.report('separation', null);
      const out = path.join(context.jobDir, 'out');
      const aborted = () => { void stop(); };
      signal.addEventListener('abort', aborted, { once: true });
      try {
        async function execute(command, args) {
          child = spawn(command, args, { cwd: context.jobDir, stdio: ['ignore', 'ignore', 'pipe'] });
          const running = child;
          let errorText = '';
          running.stderr.on('data', chunk => { errorText = (errorText + chunk.toString()).slice(-8192); });
          const code = await new Promise((resolve, reject) => { running.once('exit', resolve); running.once('error', reject); });
          if (signal.aborted) throw signal.reason;
          if (code !== 0) throw Object.assign(new Error('Faer separation failed'), /CUDA out of memory|CUDA error|CUBLAS_STATUS_ALLOC_FAILED/i.test(errorText) ? { code: 'FAER_GPU_OOM' } : {});
          child = null;
        }
        await execute(python, ['-m', 'demucs.separate', '--two-stems=vocals', '-n', 'htdemucs', '-d', 'cuda', '-o', out, input]);
        context.report('verifying', null);
        const folder = path.join(out, 'htdemucs', 'input');
        const aligned = path.join(context.jobDir, 'aligned');
        await fs.mkdir(aligned);
        for (const stem of ['vocals', 'no_vocals']) await execute('ffmpeg', ['-nostdin', '-hide_banner', '-loglevel', 'error', '-y', '-i', path.join(folder, `${stem}.wav`), '-af', `aresample=${sampleRate},apad,atrim=end_sample=${frames}`, '-ac', String(channels), '-ar', String(sampleRate), '-c:a', 'pcm_s16le', path.join(aligned, `${stem}.wav`)]);
        const vocals = await context.storeArtifact(path.join(aligned, 'vocals.wav'));
        const backing = await context.storeArtifact(path.join(aligned, 'no_vocals.wav'));
        return { outputs: [{ role: 'vocals', ...vocals }, { role: 'backing', ...backing }] };
      } finally {
        signal.removeEventListener('abort', aborted);
        await stop();
        child = null;
      }
    },
  };
}
