const TRANSIENT_GPU_CODES = new Set(['FAER_GPU_BUSY', 'FAER_GPU_OOM', 'FAER_GPU_UNAVAILABLE']);

function pause(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(signal.reason);
    const timer = setTimeout(done, ms);
    function done() { signal.removeEventListener('abort', aborted); resolve(); }
    function aborted() { clearTimeout(timer); signal.removeEventListener('abort', aborted); reject(signal.reason); }
    signal.addEventListener('abort', aborted, { once: true });
  });
}

// Workers own their model/process memory. A descriptor may share one key and one
// worker instance across capabilities (for example ACE song and ACE voice).
export function createGpuManager({ workers, warmMs, retryDelaysMs }) {
  const entries = Object.fromEntries(Object.entries(workers).map(([name, value]) => [name, typeof value === 'function' ? { key: name, run: value, unload: async () => {} } : value]));
  if (Object.values(entries).some(worker => !worker || typeof worker.key !== 'string' || !worker.key || typeof worker.run !== 'function' || typeof worker.unload !== 'function')) throw new Error('Faer GPU worker configuration is invalid');
  const keepWarm = warmMs ?? 60000;
  const delays = retryDelaysMs ?? [1000, 3000, 6000];
  if (!Number.isSafeInteger(keepWarm) || keepWarm < 0 || !Array.isArray(delays) || delays.some(ms => !Number.isSafeInteger(ms) || ms < 0)) throw new Error('Faer GPU policy is invalid');
  let held = null;
  let timer = null;
  let busy = false;
  let closed = false;
  let faulted = false;
  let unloading = null;

  function clearIdle() { if (timer) clearTimeout(timer); timer = null; }

  async function unload() {
    clearIdle();
    if (unloading) return unloading;
    const previous = held;
    held = null;
    if (previous) {
      unloading = (async () => {
        try { await previous.unload(); }
        catch { faulted = true; throw new Error('Faer GPU worker could not unload'); }
      })();
      try { await unloading; } finally { unloading = null; }
    }
  }

  function idle() {
    if (keepWarm === 0) return unload();
    timer = setTimeout(() => { if (!busy) void unload().catch(() => {}); }, keepWarm);
    timer.unref?.();
    return Promise.resolve();
  }

  return {
    ready(capability) { return !closed && !faulted && Boolean(entries[capability]); },
    async run(capability, request, signal, context) {
      const worker = entries[capability];
      if (closed || faulted || busy || !worker || typeof worker.run !== 'function' || typeof worker.unload !== 'function' || typeof worker.key !== 'string') throw new Error('Faer GPU worker is unavailable');
      busy = true;
      clearIdle();
      try {
        if (unloading) await unloading;
        if (faulted) throw new Error('Faer GPU worker is unavailable');
        if (held && (held.key !== worker.key || held !== worker)) await unload();
        held = worker;
        for (let attempt = 0;; attempt++) {
          if (signal.aborted) throw signal.reason;
          try {
            return await worker.run(request, signal, context);
          } catch (error) {
            await unload();
            if (signal.aborted || !TRANSIENT_GPU_CODES.has(error?.code) || attempt >= delays.length) throw error;
            context.report?.('preparing', null);
            await pause(delays[attempt], signal);
            held = worker;
          }
        }
      } finally {
        busy = false;
        if (signal.aborted) await unload();
        else if (held) await idle();
      }
    },
    async close() {
      closed = true;
      if (busy) throw new Error('Faer GPU job is still running');
      await unload();
    },
  };
}
