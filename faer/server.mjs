import http from 'node:http';
import https from 'node:https';
import { createHash, randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import { constants as fsConstants, createReadStream } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createGpuManager } from './gpu-manager.mjs';
import { createAceWorker } from './ace-worker.mjs';
import { createSeparationWorker } from './separation-worker.mjs';
import { createVoiceWorker } from './voice-worker.mjs';

const HASH = /^[a-f0-9]{64}$/;
const GIB = 1024 ** 3;
const STAGES = new Set(['preparing', 'planning', 'synthesis', 'isolation', 'voice', 'separation', 'verifying']);
const FAILURE_CODES = new Set(['FAER_GPU_BUSY', 'FAER_GPU_OOM', 'FAER_GPU_UNAVAILABLE', 'FAER_ACE_FAILED', 'FAER_ARTIFACT_INVALID', 'FAER_WORKER_FAILED']);
const RENDER_CACHE_VERSION = 1;
const RENDER_CACHE_TTL_MS = 30 * 24 * 60 * 60 * 1000;

function json(res, status, value) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
  res.end(JSON.stringify(value));
}

function safeResult(result) {
  if (!result || !Array.isArray(result.outputs) || result.outputs.length < 1 || result.outputs.length > 8) throw new Error('worker result is invalid');
  const outputs = result.outputs.map(output => {
    if (!output || typeof output.role !== 'string' || !/^[a-z][a-z0-9-]{0,39}$/.test(output.role) || !HASH.test(output.hash) || !Number.isSafeInteger(output.size) || output.size < 1) throw new Error('worker artifact is invalid');
    return { role: output.role, hash: output.hash, size: output.size };
  });
  return { outputs };
}

function canonical(value) {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object') return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
  return JSON.stringify(value);
}

function renderCacheKey(request, epoch, models) {
  return createHash('sha256').update(canonical({ version: RENDER_CACHE_VERSION, epoch, models, capability: request.capability, inputs: request.inputs, render: request.render })).digest('hex');
}

async function body(req) {
  let text = '';
  for await (const chunk of req) {
    text += chunk;
    if (text.length > 65536) throw new Error('request too large');
  }
  return JSON.parse(text);
}

async function digest(file) {
  const hash = createHash('sha256');
  const handle = await fs.open(file, 'r');
  try {
    for await (const chunk of handle.createReadStream()) hash.update(chunk);
  } finally {
    await handle.close();
  }
  return hash.digest('hex');
}

export async function createFaerServer({ dataDir, modelDir = '', token = '', tls = null, quotaBytes = 20 * GIB, maxAssetBytes = 4 * GIB, maxJobs, workers = {}, renderCacheBytes = 4 * GIB, renderCacheEntries = 128, renderCacheEpoch = '1' }) {
  if (!dataDir) throw new Error('Faer requires a data directory');
  const jobLimit = maxJobs ?? 128;
  if (!Number.isSafeInteger(jobLimit) || jobLimit < 1) throw new Error('Faer job limit is invalid');
  if (!Number.isSafeInteger(renderCacheBytes) || renderCacheBytes < 0 || !Number.isSafeInteger(renderCacheEntries) || renderCacheEntries < 0 || typeof renderCacheEpoch !== 'string') throw new Error('Faer render cache limits are invalid');
  const assets = path.join(dataDir, 'assets');
  const uploads = path.join(dataDir, 'uploads');
  await fs.mkdir(assets, { recursive: true });
  await fs.mkdir(uploads, { recursive: true });
  const jobFiles = path.join(dataDir, 'jobs');
  const renderCacheFile = path.join(dataDir, 'render-cache.json');
  await fs.mkdir(jobFiles, { recursive: true });
  const jobs = new Map();
  const renderCache = new Map();
  const cancelledBeforeSubmit = new Set();
  const gpu = createGpuManager({ workers });
  let uploadBusy = false;
  let activeJob = null;
  let activePromise = null;
  let draining = false;
  const queue = [];

  async function modelCatalog() {
    if (!modelDir) return [];
    let files;
    try { files = await fs.readdir(modelDir, { withFileTypes: true }); }
    catch (error) { if (error.code === 'ENOENT') return []; throw error; }
    return (await Promise.all(files.filter(file => file.isFile() && file.name.endsWith('.gguf')).map(async file => {
      const role = /^acestep-5Hz-lm-/i.test(file.name) ? 'language-model' : /^Qwen3-Embedding-/i.test(file.name) ? 'text-encoder' : /^vae-/i.test(file.name) ? 'vae' : /^acestep-v15-/i.test(file.name) ? 'synthesizer' : 'unknown';
      const info = await fs.stat(path.join(modelDir, file.name));
      return { file: file.name, role, sizeBytes: info.size };
    }))).sort((a, b) => a.file.localeCompare(b.file));
  }

  async function modelIdentity() {
    if (!modelDir) return [];
    let files;
    try { files = await fs.readdir(modelDir, { withFileTypes: true }); }
    catch (error) { if (error.code === 'ENOENT') return []; throw error; }
    return (await Promise.all(files.filter(file => file.isFile() && file.name.endsWith('.gguf')).map(async file => {
      const info = await fs.stat(path.join(modelDir, file.name));
      return [file.name, info.size, info.mtimeMs];
    }))).sort((a, b) => a[0].localeCompare(b[0]));
  }

  function publicJob(job) {
    return { id: job.id, state: job.state, queuePosition: job.state === 'queued' ? queue.indexOf(job) + 1 : null, progress: job.state === 'running' ? job.progress : null, result: job.state === 'done' ? job.result : null, failure: job.state === 'failed' ? job.failure : null, cacheHit: job.cacheHit === true };
  }

  function report(job, stage, fraction) {
    if (job.state !== 'running' || !STAGES.has(stage) || (fraction !== null && (typeof fraction !== 'number' || !Number.isFinite(fraction) || fraction < 0 || fraction > 1))) throw new Error('worker progress is invalid');
    job.progress = { stage, fraction };
  }

  async function assetState(hash) {
    const file = path.join(assets, hash);
    const part = path.join(uploads, `${hash}.part`);
    const complete = await fs.stat(file).catch(() => null);
    const partial = await fs.stat(part).catch(() => null);
    return { present: Boolean(complete), received: partial?.size ?? 0, size: complete?.size ?? null };
  }

  async function verifiedAsset(hash, expectedSize) {
    const file = path.join(assets, hash);
    const info = await fs.stat(file).catch(() => null);
    return Boolean(info?.isFile() && (expectedSize === undefined || info.size === expectedSize) && await digest(file) === hash);
  }

  async function usedBytes() {
    let total = 0;
    for (const dir of [assets, uploads]) {
      for (const name of await fs.readdir(dir)) total += (await fs.stat(path.join(dir, name))).size;
    }
    for (const name of await fs.readdir(jobFiles)) {
      const folder = path.join(jobFiles, name);
      for (const file of await fs.readdir(folder)) total += (await fs.stat(path.join(folder, file))).size;
    }
    total += (await fs.stat(renderCacheFile).catch(() => null))?.size ?? 0;
    return total;
  }

  let cacheWrite = Promise.resolve();
  function persistRenderCache() {
    const snapshot = JSON.stringify({ version: RENDER_CACHE_VERSION, entries: [...renderCache].map(([key, entry]) => ({ key, ...entry })) });
    cacheWrite = cacheWrite.catch(() => {}).then(async () => {
      const temp = `${renderCacheFile}.${randomUUID()}.tmp`;
      try { await fs.writeFile(temp, snapshot, { flag: 'wx' }); await fs.rename(temp, renderCacheFile); }
      finally { await fs.unlink(temp).catch(() => {}); }
    }).catch(() => {});
    return cacheWrite;
  }

  async function evictRenderEntry(key) {
    renderCache.delete(key);
  }

  async function pruneRenderCache() {
    const now = Date.now();
    for (const [key, entry] of renderCache) if (!Number.isSafeInteger(entry.lastUsedAt) || now - entry.lastUsedAt > RENDER_CACHE_TTL_MS) await evictRenderEntry(key);
    const total = () => [...renderCache.values()].reduce((sum, entry) => sum + entry.bytes, 0);
    while (renderCache.size > renderCacheEntries || total() > renderCacheBytes) {
      const oldest = [...renderCache].sort((a, b) => a[1].lastUsedAt - b[1].lastUsedAt)[0];
      if (!oldest) break;
      await evictRenderEntry(oldest[0]);
    }
  }

  async function loadRenderCache() {
    let stored;
    try { stored = JSON.parse(await fs.readFile(renderCacheFile, 'utf8')); }
    catch (error) { if (error.code === 'ENOENT' || error instanceof SyntaxError) return; throw error; }
    if (stored?.version !== RENDER_CACHE_VERSION || !Array.isArray(stored.entries)) return;
    for (const entry of stored.entries) {
      if (typeof entry?.key !== 'string' || !HASH.test(entry.key) || !Number.isSafeInteger(entry.bytes) || !Number.isSafeInteger(entry.lastUsedAt)) continue;
      try {
        const clean = safeResult({ outputs: entry.outputs });
        if (clean.outputs.reduce((sum, output) => sum + output.size, 0) !== entry.bytes) continue;
        renderCache.set(entry.key, { outputs: clean.outputs, bytes: entry.bytes, lastUsedAt: entry.lastUsedAt });
      } catch { /* A malformed disposable cache entry is ignored. */ }
    }
    await pruneRenderCache();
    await persistRenderCache().catch(() => {});
  }

  async function cachedRender(key) {
    const entry = renderCache.get(key);
    if (!entry) return null;
    if (Date.now() - entry.lastUsedAt > RENDER_CACHE_TTL_MS) {
      await evictRenderEntry(key);
      await persistRenderCache();
      return null;
    }
    for (const output of entry.outputs) {
      if (!await verifiedAsset(output.hash, output.size)) {
        await evictRenderEntry(key);
        await persistRenderCache();
        return null;
      }
    }
    entry.lastUsedAt = Date.now();
    await persistRenderCache();
    return { outputs: entry.outputs };
  }

  async function rememberRender(key, result) {
    const outputs = safeResult(result).outputs;
    const bytes = outputs.reduce((sum, output) => sum + output.size, 0);
    if (renderCacheEntries === 0 || renderCacheBytes === 0 || bytes > renderCacheBytes) return;
    renderCache.set(key, { outputs, bytes, lastUsedAt: Date.now() });
    await pruneRenderCache();
    await persistRenderCache();
  }

  async function hasRoom(bytes) {
    return await usedBytes() + bytes <= quotaBytes;
  }

  async function storeArtifact(job, file) {
    const resolved = path.resolve(file);
    const ownFolder = path.join(jobFiles, job.id);
    if (!resolved.startsWith(ownFolder + path.sep) || !(await fs.realpath(resolved)).startsWith(ownFolder + path.sep)) throw new Error('artifact must be in its job folder');
    const info = await fs.lstat(resolved);
    if (!info.isFile() || info.size < 1 || info.size > maxAssetBytes || !await hasRoom(info.size)) throw new Error('artifact exceeds Faer storage limits');
    const hash = await digest(resolved);
    if (!(await assetState(hash)).present) {
      const temp = path.join(uploads, `${randomUUID()}.part`);
      try {
        await fs.copyFile(resolved, temp, fsConstants.COPYFILE_EXCL);
        if (await digest(temp) !== hash) throw new Error('artifact changed during copy');
        await fs.rename(temp, path.join(assets, hash));
      } finally {
        await fs.unlink(temp).catch(() => {});
      }
    }
    return { hash, size: info.size };
  }

  async function upload(req, res, hash) {
    if (uploadBusy) return json(res, 409, { error: 'asset upload is busy' });
    uploadBusy = true;
    try {
      const total = Number(req.headers['x-faer-total']);
      const offset = Number(req.headers['x-faer-offset']);
      const length = Number(req.headers['content-length']);
      if (![total, offset, length].every(Number.isSafeInteger) || total < 1 || total > maxAssetBytes || offset < 0 || length < 1 || length > 8 * 1024 * 1024 || offset + length > total) return json(res, 400, { error: 'invalid upload range' });
      const state = await assetState(hash);
      if (state.present) {
        if (await verifiedAsset(hash, state.size)) return json(res, 200, { present: true, received: state.size });
        await fs.unlink(path.join(assets, hash));
      }
      if (offset !== state.received) return json(res, 409, { error: 'offset mismatch', received: state.received });
      if (!await hasRoom(length)) return json(res, 507, { error: 'Faer storage limit reached' });
      const part = path.join(uploads, `${hash}.part`);
      const handle = await fs.open(part, offset === 0 ? 'w' : 'r+');
      let written = 0;
      try {
        for await (const chunk of req) {
          written += chunk.length;
          if (written > length) throw new Error('upload chunk exceeds declared length');
          let cursor = 0;
          while (cursor < chunk.length) {
            const { bytesWritten } = await handle.write(chunk, cursor, chunk.length - cursor, offset + written - chunk.length + cursor);
            cursor += bytesWritten;
          }
        }
      } finally {
        await handle.close();
      }
      if (written !== length) return json(res, 400, { error: 'incomplete upload chunk', received: offset + written });
      const received = offset + written;
      if (received < total) return json(res, 202, { present: false, received });
      if (await digest(part) !== hash) {
        await fs.unlink(part);
        return json(res, 422, { error: 'asset hash mismatch' });
      }
      await fs.rename(part, path.join(assets, hash));
      return json(res, 201, { present: true, received });
    } finally {
      uploadBusy = false;
    }
  }

  function nextJob() {
    if (draining || activeJob || !queue.length) return;
    const job = queue.shift();
    if (job.state === 'cancelled') return nextJob();
    activeJob = job;
    job.state = 'running';
    job.progress = { stage: 'preparing', fraction: null };
    const folder = path.join(jobFiles, job.id);
    activePromise = Promise.resolve().then(async () => {
      await fs.mkdir(folder);
      return gpu.run(job.capability, job.request, job.abort.signal, { jobDir: folder, storeArtifact: file => storeArtifact(job, file), report: (stage, fraction) => report(job, stage, fraction) });
    }).then(async result => {
      if (job.state !== 'cancelled') {
        const clean = safeResult(result);
        for (const output of clean.outputs) if (!(await verifiedAsset(output.hash, output.size))) throw new Error('worker artifact is unavailable');
        job.result = clean;
        job.state = 'done';
        if (job.cacheKey) await rememberRender(job.cacheKey, clean).catch(() => {});
      }
    }).catch(error => {
      if (job.state !== 'cancelled') {
        job.failure = { code: FAILURE_CODES.has(error?.code) ? error.code : 'FAER_WORKER_FAILED', stage: job.progress?.stage ?? 'preparing', reason: error?.message || String(error), details: error?.stack || null };
        console.error(`[Faer] job ${job.id} failed (${job.failure.code} at ${job.failure.stage}): ${error?.stack || error}`);
        job.state = 'failed';
      }
    }).finally(async () => { await fs.rm(folder, { recursive: true, force: true }).catch(() => {}); activeJob = null; nextJob(); });
  }

  const handleRequest = async (req, res) => {
    try {
      if (token && req.headers.authorization !== `Bearer ${token}`) return json(res, 401, { error: 'unauthorized' });
      const url = new URL(req.url, 'http://localhost');
      if (req.method === 'GET' && url.pathname === '/v1/status') return json(res, 200, { protocol: 1, storage: { usedBytes: await usedBytes(), quotaBytes }, capabilities: Object.fromEntries(['song', 'voice-ace', 'voice-clone', 'separation'].map(name => [name, { ready: gpu.ready(name) }])), models: await modelCatalog() });
      const asset = /^\/v1\/assets\/([a-f0-9]{64})$/.exec(url.pathname);
      if (asset && HASH.test(asset[1])) {
        if (req.method === 'GET') {
          const state = await assetState(asset[1]);
          return json(res, 200, { ...state, present: state.present && await verifiedAsset(asset[1], state.size) });
        }
        if (req.method === 'PUT') return await upload(req, res, asset[1]);
      }
      const download = /^\/v1\/assets\/([a-f0-9]{64})\/bytes$/.exec(url.pathname);
      if (download && req.method === 'GET') {
        const state = await assetState(download[1]);
        if (!state.present || !await verifiedAsset(download[1], state.size)) return json(res, 404, { error: 'asset unavailable' });
        res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': state.size, 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
        createReadStream(path.join(assets, download[1])).pipe(res);
        return;
      }
      if (req.method === 'POST' && url.pathname === '/v1/jobs') {
        if (draining) return json(res, 503, { error: 'Faer is shutting down' });
        const request = await body(req);
        if (!request || typeof request.capability !== 'string' || !gpu.ready(request.capability) || !Array.isArray(request.inputs) || request.inputs.length > 16 || request.inputs.some(input => typeof input !== 'string' || !HASH.test(input))) return json(res, 400, { error: 'capability or inputs unavailable' });
        if (request.id !== undefined && !/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(request.id)) return json(res, 400, { error: 'invalid job id' });
        const id = request.id ?? randomUUID();
        if (cancelledBeforeSubmit.has(id)) return json(res, 409, { error: 'job was cancelled before submission' });
        if (jobs.has(id)) return json(res, 200, publicJob(jobs.get(id)));
        for (const input of request.inputs) if (!await verifiedAsset(input)) return json(res, 409, { error: 'required asset unavailable' });
        for (const [id, old] of jobs) if (jobs.size >= jobLimit && old !== activeJob && !queue.includes(old) && ['done', 'failed', 'cancelled'].includes(old.state)) jobs.delete(id);
        if (jobs.size >= jobLimit) return json(res, 429, { error: 'Faer job queue is full' });
        const cacheable = ['song', 'voice-ace'].includes(request.capability) && request.forceRender !== true;
        const key = ['song', 'voice-ace'].includes(request.capability) ? renderCacheKey(request, renderCacheEpoch, await modelIdentity()) : null;
        if (cacheable && key) {
          const cached = await cachedRender(key);
          if (cached) {
            const job = { id, capability: request.capability, request, state: 'done', progress: null, result: cached, cacheHit: true };
            jobs.set(id, job);
            await persistRenderCache();
            return json(res, 200, publicJob(job));
          }
        }
        const job = { id, capability: request.capability, request, cacheKey: key, state: 'queued', progress: null, result: null, abort: new AbortController() };
        jobs.set(job.id, job); queue.push(job); nextJob();
        return json(res, 202, publicJob(job));
      }
      const jobRoute = /^\/v1\/jobs\/([a-f0-9-]{36})$/.exec(url.pathname);
      if (jobRoute) {
        const job = jobs.get(jobRoute[1]);
        if (!job && req.method === 'DELETE') {
          cancelledBeforeSubmit.add(jobRoute[1]);
          if (cancelledBeforeSubmit.size > jobLimit) cancelledBeforeSubmit.delete(cancelledBeforeSubmit.values().next().value);
          return json(res, 200, { id: jobRoute[1], state: 'cancelled', queuePosition: null, progress: null, result: null });
        }
        if (!job) return json(res, 404, { error: 'job unavailable' });
        if (req.method === 'GET') return json(res, 200, publicJob(job));
        if (req.method === 'DELETE') {
          if (job.state === 'queued' || job.state === 'running') { job.state = 'cancelled'; job.abort.abort(); }
          return json(res, 200, publicJob(job));
        }
      }
      return json(res, 404, { error: 'route unavailable' });
    } catch {
      if (!res.headersSent) json(res, 400, { error: 'invalid request' });
      else res.destroy();
    }
  };
  const server = tls ? https.createServer(tls, handleRequest) : http.createServer(handleRequest);
  await loadRenderCache();
  server.faerShutdown = async () => {
    draining = true;
    for (const job of queue) { job.state = 'cancelled'; job.abort.abort(); }
    if (activeJob) { activeJob.state = 'cancelled'; activeJob.abort.abort(); }
    if (activePromise) await activePromise;
    await gpu.close();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  };
  return server;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const bind = process.env.FAER_BIND || '127.0.0.1';
  const token = process.env.FAER_TOKEN_FILE ? (await fs.readFile(process.env.FAER_TOKEN_FILE, 'utf8')).trim() : '';
  const tls = process.env.FAER_TLS_CERT_FILE && process.env.FAER_TLS_KEY_FILE ? { cert: await fs.readFile(process.env.FAER_TLS_CERT_FILE), key: await fs.readFile(process.env.FAER_TLS_KEY_FILE) } : null;
  if (!['127.0.0.1', '::1', 'localhost'].includes(bind) && (!token || !tls || process.env.FAER_ALLOW_REMOTE !== '1')) throw new Error('non-loopback Faer requires TLS, a token and an explicit remote setting');
  const ace = process.env.FAER_ACE_BIN && process.env.FAER_ACE_MODELS ? createAceWorker({ bin: process.env.FAER_ACE_BIN, models: process.env.FAER_ACE_MODELS, plugin: path.join(path.dirname(fileURLToPath(import.meta.url)), 'acestep', 'plugin.mjs'), node: process.execPath, assets: path.join(process.env.FAER_DATA_DIR, 'assets') }) : null;
  const separation = process.env.FAER_SEPARATION_PYTHON ? createSeparationWorker({ python: process.env.FAER_SEPARATION_PYTHON, assets: path.join(process.env.FAER_DATA_DIR, 'assets') }) : null;
  const voice = process.env.FAER_VOICE_MODELS && process.env.FAER_VOICE_PYTHON_ROOT ? createVoiceWorker({ node: process.execPath, plugin: path.join(path.dirname(fileURLToPath(import.meta.url)), 'voice-clone', 'plugin.mjs'), modelRoot: process.env.FAER_VOICE_MODELS, pythonRoot: process.env.FAER_VOICE_PYTHON_ROOT, assets: path.join(process.env.FAER_DATA_DIR, 'assets') }) : null;
  const cacheNumber = (name, fallback) => process.env[name] === undefined ? fallback : Number.isSafeInteger(Number(process.env[name])) && Number(process.env[name]) >= 0 ? Number(process.env[name]) : (() => { throw new Error(`${name} is invalid`); })();
  const server = await createFaerServer({ dataDir: process.env.FAER_DATA_DIR, modelDir: process.env.FAER_ACE_MODELS, token, tls, renderCacheBytes: cacheNumber('FAER_RENDER_CACHE_BYTES', 4 * GIB), renderCacheEntries: cacheNumber('FAER_RENDER_CACHE_ENTRIES', 128), renderCacheEpoch: process.env.FAER_RENDER_CACHE_EPOCH || '1', workers: { ...(ace ? { song: ace, 'voice-ace': ace } : {}), ...(separation ? { separation } : {}), ...(voice ? { 'voice-clone': voice } : {}) } });
  server.listen(Number(process.env.FAER_PORT || 3888), bind, () => process.stdout.write('Faer listening\n'));
  for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, () => { void server.faerShutdown().catch(() => { process.exitCode = 1; }); });
}
