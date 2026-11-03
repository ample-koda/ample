import { test } from 'node:test';
import assert from 'node:assert/strict';
import { aceServerMemoryFlags } from './ace-worker.mjs';
import { createGpuManager } from './gpu-manager.mjs';

test('ACE uses strict model eviction on every GPU and smaller VAE tiles below 20 GiB', () => {
  assert.deepEqual(aceServerMemoryFlags(16376), ['--vae-chunk', '512']);
  assert.deepEqual(aceServerMemoryFlags(20479), ['--vae-chunk', '512']);
  assert.deepEqual(aceServerMemoryFlags(20480), []);
  assert.deepEqual(aceServerMemoryFlags(null), ['--vae-chunk', '512']);
});

test('Faer retains a compatible GPU worker and unloads it before changing engines', async () => {
  const events = [];
  const ace = { key: 'ace', run: async () => { events.push('ace-run'); return 'ace'; }, unload: async () => { events.push('ace-unload'); } };
  const stem = { key: 'stem', run: async () => { events.push('stem-run'); return 'stem'; }, unload: async () => { events.push('stem-unload'); } };
  const gpu = createGpuManager({ workers: { song: ace, 'voice-ace': ace, separation: stem }, warmMs: 1000, retryDelaysMs: [] });
  const signal = new AbortController().signal;
  assert.equal(await gpu.run('song', {}, signal, {}), 'ace');
  assert.equal(await gpu.run('voice-ace', {}, signal, {}), 'ace');
  assert.equal(await gpu.run('separation', {}, signal, {}), 'stem');
  await gpu.close();
  assert.deepEqual(events, ['ace-run', 'ace-run', 'ace-unload', 'stem-run', 'stem-unload']);
});

test('Faer unloads and retries only coded transient GPU failures', async () => {
  let attempts = 0;
  let unloads = 0;
  const stages = [];
  const worker = { key: 'ace', run: async () => {
    attempts++;
    if (attempts < 3) throw Object.assign(new Error('synthetic GPU busy'), { code: 'FAER_GPU_BUSY' });
    return 'done';
  }, unload: async () => { unloads++; } };
  const gpu = createGpuManager({ workers: { song: worker }, warmMs: 1000, retryDelaysMs: [1, 1] });
  assert.equal(await gpu.run('song', {}, new AbortController().signal, { report: stage => stages.push(stage) }), 'done');
  assert.equal(attempts, 3);
  assert.equal(unloads, 2);
  assert.deepEqual(stages, ['preparing', 'preparing']);
  await gpu.close();
  assert.equal(unloads, 3);
});

test('Faer does not retry unrelated failures or continue after cancellation', async () => {
  let attempts = 0;
  const worker = { key: 'ace', run: async () => { attempts++; throw new Error('synthetic invalid request'); }, unload: async () => {} };
  const gpu = createGpuManager({ workers: { song: worker }, warmMs: 0, retryDelaysMs: [1, 1] });
  await assert.rejects(gpu.run('song', {}, new AbortController().signal, {}), /synthetic invalid request/);
  assert.equal(attempts, 1);
  await gpu.close();

  const abort = new AbortController();
  let busyAttempts = 0;
  const busy = { key: 'ace', run: async () => { busyAttempts++; throw Object.assign(new Error('synthetic GPU busy'), { code: 'FAER_GPU_OOM' }); }, unload: async () => {} };
  const cancelled = createGpuManager({ workers: { song: busy }, warmMs: 0, retryDelaysMs: [100] });
  await assert.rejects(cancelled.run('song', {}, abort.signal, { report: () => abort.abort() }), { name: 'AbortError' });
  assert.equal(busyAttempts, 1);
  await cancelled.close();
});

test('Faer unloads a warm GPU worker after its idle period', async () => {
  let unloads = 0;
  const worker = { key: 'ace', run: async () => 'done', unload: async () => { unloads++; } };
  const gpu = createGpuManager({ workers: { song: worker }, warmMs: 10, retryDelaysMs: [] });
  await gpu.run('song', {}, new AbortController().signal, {});
  await new Promise(resolve => setTimeout(resolve, 40));
  assert.equal(unloads, 1);
  await gpu.close();
});

test('Faer stops accepting GPU work after an unload failure', async () => {
  const worker = { key: 'ace', run: async () => 'done', unload: async () => { throw new Error('synthetic unload failure'); } };
  const gpu = createGpuManager({ workers: { song: worker }, warmMs: 10, retryDelaysMs: [] });
  await gpu.run('song', {}, new AbortController().signal, {});
  await new Promise(resolve => setTimeout(resolve, 40));
  assert.equal(gpu.ready('song'), false);
  await assert.rejects(gpu.run('song', {}, new AbortController().signal, {}), /unavailable/);
});

test('Faer waits for an in-flight idle unload before starting another GPU worker', async () => {
  const events = [];
  let release;
  const waiting = new Promise(resolve => { release = resolve; });
  let unloading;
  const unloadingStarted = new Promise(resolve => { unloading = resolve; });
  const first = { key: 'first', run: async () => 'done', unload: async () => { events.push('unload-start'); unloading(); await waiting; events.push('unload-end'); } };
  const second = { key: 'second', run: async () => { events.push('second-run'); return 'done'; }, unload: async () => {} };
  const gpu = createGpuManager({ workers: { song: first, separation: second }, warmMs: 1, retryDelaysMs: [] });
  await gpu.run('song', {}, new AbortController().signal, {});
  await unloadingStarted;
  const next = gpu.run('separation', {}, new AbortController().signal, {});
  await Promise.resolve();
  assert.deepEqual(events, ['unload-start']);
  release();
  await next;
  assert.deepEqual(events, ['unload-start', 'unload-end', 'second-run']);
  await gpu.close();
});
