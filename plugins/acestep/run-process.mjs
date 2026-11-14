// Runs a child process (an acestep.cpp tool or Demucs) without blocking the event loop; cancel and the time limit stop it.
// The returned promise rejects with an Error whose kind is 'timeout', 'cancelled', 'start' (the program could not be started; code is the system error code) or 'exit' (exitCode is set); stderr holds the last 3000 characters the program wrote there.
import { spawn } from 'node:child_process';

export function runProcess(exe, args, { cwd, ctx, timeoutMs, pollMs = 500 }) {
  return new Promise((resolve, reject) => {
    let settled = false, err = '';
    const child = spawn(exe, args, { cwd, stdio: ['ignore', 'ignore', 'pipe'] });
    child.stderr.on('data', d => { err = (err + d).slice(-3000); });
    const stop = () => {
      // Python and the acestep.cpp tools may start helpers of their own: on Windows the whole tree of this one child is stopped.
      if (process.platform === 'win32' && child.pid) { try { spawn('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore' }).on('error', () => {}); } catch { /* the plain kill below still runs */ } }
      child.kill();
    };
    const limit = setTimeout(() => finish('timeout', 'timed out', true), timeoutMs);
    const watch = setInterval(() => { if (ctx.isCancelled()) { finish('cancelled', 'cancelled', true); } }, pollMs);
    function finish(kind, message, kill, extra) {
      if (settled) { return; }
      settled = true;
      clearTimeout(limit);
      clearInterval(watch);
      if (kill) { stop(); }
      if (!kind) { resolve(); } else { reject(Object.assign(new Error(message), { kind, stderr: err }, extra)); }
    }
    child.on('error', e => finish('start', e.message, false, { code: e.code }));
    child.on('close', code => { if (code === 0) { finish(null); } else { finish('exit', 'exit code ' + code, false, { exitCode: code }); } });
  });
}
