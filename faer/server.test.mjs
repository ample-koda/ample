import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createFaerServer } from './server.mjs';

test('Faer rejects a late submission after its client-assigned job id was cancelled', async () => {
  const dataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'ample-faer-test-'));
  let runs = 0;
  const server = await createFaerServer({ dataDir, workers: { song: async () => { runs++; return { outputs: [] }; } } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const id = randomUUID();
  try {
    assert.equal((await fetch(`${base}/v1/jobs/${id}`, { method: 'DELETE' })).status, 200);
    assert.equal((await fetch(`${base}/v1/jobs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id, capability: 'song', inputs: [] }) })).status, 409);
    assert.equal(runs, 0);
  } finally {
    await server.faerShutdown();
  }
});

test('Faer accepts resumed verified assets and cancels owned jobs', async () => {
  const dataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'ample-faer-test-'));
  const modelsDir = path.join(dataDir, 'models');
  await fs.mkdir(modelsDir);
  await fs.writeFile(path.join(modelsDir, 'acestep-v15-xl-sft-Q5_K_M.gguf'), 'model');
  await fs.writeFile(path.join(modelsDir, 'acestep-5Hz-lm-4B-Q8_0.gguf'), 'lm');
  await fs.writeFile(path.join(modelsDir, 'custom-audio-model.gguf'), 'other');
  await fs.writeFile(path.join(modelsDir, 'other-model.bin'), 'ignore');
  await fs.writeFile(path.join(modelsDir, 'acestep-future.partial'), 'ignore');
  let started;
  const running = new Promise(resolve => { started = resolve; });
  const server = await createFaerServer({ dataDir, token: 'tok-tst', quotaBytes: 1024, workers: { song: async (_request, signal, context) => {
    context.report('synthesis', 0.25);
    started();
    await new Promise(resolve => signal.addEventListener('abort', resolve, { once: true }));
    return { artifact: 'synthetic' };
  } }, maxJobs: 2, modelDir: modelsDir });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const headers = { Authorization: 'Bearer tok-tst' };
  try {
    assert.equal((await fetch(`${base}/v1/status`)).status, 401);
    const status = await (await fetch(`${base}/v1/status`, { headers })).json();
    assert.equal(status.capabilities.song.ready, true);
    assert.equal(status.capabilities.separation.ready, false);
    assert.deepEqual(Object.keys(status.storage).sort(), ['quotaBytes', 'usedBytes']);
    assert.deepEqual(status.models, [
      { file: 'acestep-5Hz-lm-4B-Q8_0.gguf', role: 'language-model', sizeBytes: 2 },
      { file: 'acestep-v15-xl-sft-Q5_K_M.gguf', role: 'synthesizer', sizeBytes: 5 },
      { file: 'custom-audio-model.gguf', role: 'unknown', sizeBytes: 5 },
    ]);
    const content = Buffer.from('synthetic audio input');
    const hash = createHash('sha256').update(content).digest('hex');
    const asset = `${base}/v1/assets/${hash}`;
    const first = await fetch(asset, { method: 'PUT', headers: { ...headers, 'x-faer-total': content.length, 'x-faer-offset': 0 }, body: content.subarray(0, 8) });
    assert.equal(first.status, 202);
    assert.equal((await first.json()).received, 8);
    assert.equal((await (await fetch(asset, { headers })).json()).received, 8);
    const wrongOffset = await fetch(asset, { method: 'PUT', headers: { ...headers, 'x-faer-total': content.length, 'x-faer-offset': 0 }, body: content.subarray(8) });
    assert.equal(wrongOffset.status, 409);
    const last = await fetch(asset, { method: 'PUT', headers: { ...headers, 'x-faer-total': content.length, 'x-faer-offset': 8 }, body: content.subarray(8) });
    assert.equal(last.status, 201);
    assert.equal((await (await fetch(asset, { headers })).json()).present, true);
    const submit = await fetch(`${base}/v1/jobs`, { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' }, body: JSON.stringify({ capability: 'song', inputs: [hash] }) });
    assert.equal(submit.status, 202);
    const job = await submit.json();
    await running;
    const active = await (await fetch(`${base}/v1/jobs/${job.id}`, { headers })).json();
    assert.deepEqual(active.progress, { stage: 'synthesis', fraction: 0.25 });
    const second = await (await fetch(`${base}/v1/jobs`, { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' }, body: JSON.stringify({ capability: 'song', inputs: [hash] }) })).json();
    assert.equal(second.queuePosition, 1);
    assert.equal((await (await fetch(`${base}/v1/jobs/${second.id}`, { headers })).json()).queuePosition, 1);
    assert.equal((await fetch(`${base}/v1/jobs`, { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' }, body: JSON.stringify({ capability: 'song', inputs: [hash] }) })).status, 429);
    assert.equal((await (await fetch(`${base}/v1/jobs/${second.id}`, { method: 'DELETE', headers })).json()).state, 'cancelled');
    const cancelled = await fetch(`${base}/v1/jobs/${job.id}`, { method: 'DELETE', headers });
    assert.equal((await cancelled.json()).state, 'cancelled');
    assert.equal((await (await fetch(`${base}/v1/jobs/${job.id}`, { headers })).json()).state, 'cancelled');
  } finally {
    await server.faerShutdown();
    await fs.rm(dataDir, { recursive: true, force: true });
  }
});

test('Faer rejects a mismatched final hash and enforces its cache quota', async () => {
  const dataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'ample-faer-test-'));
  const server = await createFaerServer({ dataDir, quotaBytes: 5 });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    const wrong = await fetch(`${base}/v1/assets/${'0'.repeat(64)}`, { method: 'PUT', headers: { 'x-faer-total': 3, 'x-faer-offset': 0 }, body: 'abc' });
    assert.equal(wrong.status, 422);
    const content = '123456';
    const hash = createHash('sha256').update(content).digest('hex');
    const full = await fetch(`${base}/v1/assets/${hash}`, { method: 'PUT', headers: { 'x-faer-total': content.length, 'x-faer-offset': 0 }, body: content });
    assert.equal(full.status, 507);
  } finally {
    await new Promise(resolve => server.close(resolve));
    await fs.rm(dataDir, { recursive: true, force: true });
  }
});

test('Faer downloads a worker artifact after a completed synthetic job', async () => {
  const dataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'ample-faer-test-'));
  const content = Buffer.from('synthetic result');
  const server = await createFaerServer({ dataDir, workers: { song: async (_request, _signal, context) => {
    const file = path.join(context.jobDir, 'result.bin');
    await fs.writeFile(file, content);
    return { outputs: [{ role: 'audio', ...await context.storeArtifact(file) }] };
  } } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    const response = await fetch(`${base}/v1/jobs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ capability: 'song', inputs: [] }) });
    assert.equal(response.status, 202);
    const { id } = await response.json();
    let job;
    for (let i = 0; i < 20; i++) {
      job = await (await fetch(`${base}/v1/jobs/${id}`)).json();
      if (job.state === 'done') break;
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    assert.equal(job.state, 'done');
    const hash = createHash('sha256').update(content).digest('hex');
    assert.equal(job.result.outputs[0].hash, hash);
    const download = await fetch(`${base}/v1/assets/${hash}/bytes`);
    assert.equal(download.status, 200);
    assert.deepEqual(Buffer.from(await download.arrayBuffer()), content);
  } finally {
    await new Promise(resolve => server.close(resolve));
    await fs.rm(dataDir, { recursive: true, force: true });
  }
});

test('Faer reuses exact completed ACE renders, persists bounded entries, and force render refreshes the cache', async () => {
  const dataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'ample-faer-test-'));
  let runs = 0;
  const worker = async (_request, _signal, context) => {
    const file = path.join(context.jobDir, 'result.bin');
    await fs.writeFile(file, `synthetic render ${++runs}`);
    return { outputs: [{ role: 'audio', ...await context.storeArtifact(file) }] };
  };
  const start = async () => {
    const server = await createFaerServer({ dataDir, renderCacheBytes: 1024, renderCacheEntries: 1, workers: { song: worker, 'voice-ace': worker } });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    return server;
  };
  const submit = async (base, render, forceRender = false) => {
    const response = await fetch(`${base}/v1/jobs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ capability: 'song', inputs: [], render, forceRender }) });
    let job = await response.json();
    for (let i = 0; i < 50 && (job.state === 'queued' || job.state === 'running'); i++) {
      await new Promise(resolve => setTimeout(resolve, 10));
      job = await (await fetch(`${base}/v1/jobs/${job.id}`)).json();
    }
    return job;
  };
  let server = await start();
  let base = `http://127.0.0.1:${server.address().port}`;
  try {
    const first = await submit(base, { seed: '42', steps: 90 });
    assert.equal(first.state, 'done');
    assert.equal(first.cacheHit, false);
    assert.equal(runs, 1);
    const hit = await submit(base, { steps: 90, seed: '42' });
    assert.equal(hit.state, 'done');
    assert.equal(hit.cacheHit, true);
    assert.equal(runs, 1);
    await submit(base, { seed: '43', steps: 90 });
    assert.equal(runs, 2);
    const evicted = await submit(base, { seed: '42', steps: 90 });
    assert.equal(evicted.cacheHit, false);
    assert.equal(runs, 3);
    const forced = await submit(base, { steps: 90, seed: '42' }, true);
    assert.equal(forced.cacheHit, false);
    assert.equal(runs, 4);
    await server.faerShutdown();
    server = await start();
    base = `http://127.0.0.1:${server.address().port}`;
    const persisted = await submit(base, { seed: '42', steps: 90 });
    assert.equal(persisted.cacheHit, true);
    assert.equal(persisted.result.outputs[0].hash, forced.result.outputs[0].hash);
    assert.equal(runs, 4);
    assert.equal(JSON.parse(await fs.readFile(path.join(dataDir, 'render-cache.json'), 'utf8')).entries.length, 1);
  } finally {
    if (server.listening) await server.faerShutdown();
    await fs.rm(dataDir, { recursive: true, force: true });
  }
});

test('Faer rejects a corrupted cached asset and accepts a verified replacement', async () => {
  const dataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'ample-faer-test-'));
  const server = await createFaerServer({ dataDir, workers: { song: async () => ({ outputs: [] }) } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const content = Buffer.from('synthetic-cache-content');
  const hash = createHash('sha256').update(content).digest('hex');
  const asset = `${base}/v1/assets/${hash}`;
  try {
    assert.equal((await fetch(asset, { method: 'PUT', headers: { 'x-faer-total': content.length, 'x-faer-offset': 0 }, body: content })).status, 201);
    await fs.writeFile(path.join(dataDir, 'assets', hash), Buffer.alloc(content.length, 0));
    assert.equal((await (await fetch(asset)).json()).present, false);
    assert.equal((await fetch(`${asset}/bytes`)).status, 404);
    assert.equal((await fetch(`${base}/v1/jobs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ capability: 'song', inputs: [hash] }) })).status, 409);
    assert.equal((await fetch(asset, { method: 'PUT', headers: { 'x-faer-total': content.length, 'x-faer-offset': 0 }, body: content })).status, 201);
    assert.equal((await (await fetch(asset)).json()).present, true);
  } finally {
    await new Promise(resolve => server.close(resolve));
    await fs.rm(dataDir, { recursive: true, force: true });
  }
});

test('Faer shutdown cancels its GPU job and unloads the worker', async () => {
  const dataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'ample-faer-test-'));
  let started;
  const running = new Promise(resolve => { started = resolve; });
  let unloads = 0;
  const worker = { key: 'ace', run: async (_request, signal) => {
    started();
    await new Promise(resolve => signal.addEventListener('abort', resolve, { once: true }));
    return { outputs: [] };
  }, unload: async () => { unloads++; } };
  const server = await createFaerServer({ dataDir, workers: { song: worker } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    assert.equal((await fetch(`${base}/v1/jobs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ capability: 'song', inputs: [] }) })).status, 202);
    await running;
    await server.faerShutdown();
    assert.equal(unloads, 1);
  } finally {
    if (server.listening) await server.faerShutdown();
    await fs.rm(dataDir, { recursive: true, force: true });
  }
});

test('Faer reports a safe failure code while logging the worker reason server-side', async () => {
  const dataDir = await fs.mkdtemp(path.join(os.tmpdir(), 'ample-faer-test-'));
  const server = await createFaerServer({ dataDir, workers: { song: async (_request, _signal, context) => {
    context.report('synthesis', 0.5);
    throw Object.assign(new Error('synthetic internal failure detail'), { code: 'FAER_ACE_FAILED' });
  } } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const originalError = console.error;
  let logged = '';
  console.error = (...parts) => { logged += parts.join(' '); };
  try {
    const response = await fetch(`${base}/v1/jobs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ capability: 'song', inputs: [] }) });
    const { id } = await response.json();
    let job;
    for (let i = 0; i < 20; i++) {
      job = await (await fetch(`${base}/v1/jobs/${id}`)).json();
      if (job.state === 'failed') break;
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    assert.deepEqual({ code: job.failure.code, stage: job.failure.stage, reason: job.failure.reason }, { code: 'FAER_ACE_FAILED', stage: 'synthesis', reason: 'synthetic internal failure detail' });
    assert.match(job.failure.details, /synthetic internal failure detail/);
    assert.equal(JSON.stringify(job).includes('synthetic internal failure detail'), true);
    assert.match(logged, /synthetic internal failure detail/);
  } finally {
    console.error = originalError;
    await server.faerShutdown();
    await fs.rm(dataDir, { recursive: true, force: true });
  }
});
