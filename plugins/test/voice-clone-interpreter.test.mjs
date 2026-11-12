// Which Python the voice-clone plugin starts: the interpreter set for the engine (pythonXtts / pythonQwen3 / pythonChatterbox), then voice-clone.python,
// then the shared venv under the model root (Chatterbox only), then the generic default. No model and no GPU: the "interpreters" are links to node itself and a
// preload script (NODE_OPTIONS) answers instead of worker.py, writing which executable it was started as. Run: node --test plugins/test/voice-clone-interpreter.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const plugin = path.join(here, '..', 'voice-clone', 'plugin.mjs');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ample-voice-interp-'));
test.after(() => fs.rmSync(root, { recursive: true, force: true }));

const spy = path.join(root, 'spy.cjs');
fs.writeFileSync(spy, `
if (/worker\\.py$/.test(process.argv[1] || '')) {
  const fs = require('fs');
  const payload = JSON.parse(fs.readFileSync(0, 'utf8'));
  fs.writeFileSync(process.env.AMPLE_SPY_MARKER, JSON.stringify({ exe: process.execPath, setting: payload.pythonSetting, engineSetting: payload.engineSetting }));
  fs.writeFileSync(payload.output, 'RIFF');
  process.exit(0);
}
`);

// a copy (or hard link) of node under a name of its own, so the executable that really ran can be told apart
const interpreter = (name) => {
  const dir = path.join(root, name);
  fs.mkdirSync(dir, { recursive: true });
  const target = path.join(dir, path.basename(process.execPath));
  try { fs.linkSync(process.execPath, target); } catch { fs.copyFileSync(process.execPath, target); }
  return target;
};

const modelRoot = path.join(root, 'models');
const touch = (...parts) => { const file = path.join(modelRoot, ...parts); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, 'x'); };
touch('XTTS-v2', 'model.pth'); touch('XTTS-v2', 'config.json');
touch('Qwen3-TTS-0.6B', 'model.safetensors'); touch('Qwen3-TTS-0.6B', 'config.json');
touch('Chatterbox-Multilingual', 've.pt');
const sharedVenv = path.join(modelRoot, 'venv', process.platform === 'win32' ? 'Scripts' : 'bin', process.platform === 'win32' ? 'python.exe' : 'python');
fs.mkdirSync(path.dirname(sharedVenv), { recursive: true });
try { fs.linkSync(process.execPath, sharedVenv); } catch { fs.copyFileSync(process.execPath, sharedVenv); }

let runs = 0;
async function render(model, settings, { withSharedVenv = true, block = {}, expectRefusal = false } = {}) {
  const jobDir = path.join(root, `job-${++runs}`);
  fs.mkdirSync(jobDir, { recursive: true });
  fs.writeFileSync(path.join(jobDir, 'sample.wav'), 'RIFF');
  const marker = path.join(jobDir, 'marker.json');
  const modelRootHere = withSharedVenv ? modelRoot : path.join(root, 'models-no-venv');
  if (!withSharedVenv && !fs.existsSync(modelRootHere)) fs.cpSync(modelRoot, modelRootHere, { recursive: true, filter: (src) => !src.includes(`${path.sep}venv`) });
  const child = spawn(process.execPath, [plugin], { env: { ...process.env, NODE_OPTIONS: `--require=${spy}`, AMPLE_SPY_MARKER: marker }, stdio: ['pipe', 'pipe', 'pipe'] });
  let timer;
  const reply = new Promise((resolve, reject) => {
    let buffer = '';
    child.stdout.on('data', (d) => { buffer += d; for (const line of buffer.split('\n')) { try { const m = JSON.parse(line); if (m.id === 1) resolve(m); } catch { /* partial line */ } } });
    child.on('error', reject);
    timer = setTimeout(() => reject(new Error('no answer')), 20000);
  });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'render', params: { block: { parameters: { model, text: 'hello', language: 'nl', ...block } }, settings: { modelRoot: modelRootHere, xttsLicenceAccepted: true, ...settings }, inputs: { reference: 'sample.wav' }, jobDir } }) + '\n');
  const answer = await reply.finally(() => clearTimeout(timer));
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'shutdown' }) + '\n');
  if (expectRefusal) return answer;
  assert.equal(answer.error, undefined, JSON.stringify(answer.error));
  return JSON.parse(fs.readFileSync(marker, 'utf8'));
}

const same = (a, b) => path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();

test('the interpreter of the engine wins over voice-clone.python and the shared venv', async () => {
  const forEngine = interpreter('py-engine'), general = interpreter('py-general');
  const ran = await render('chatterbox', { pythonChatterbox: forEngine, python: general });
  assert.ok(same(ran.exe, forEngine), ran.exe);
  assert.equal(ran.setting, 'voice-clone.pythonChatterbox');
  const xtts = await render('xtts', { pythonXtts: forEngine, python: general });
  assert.ok(same(xtts.exe, forEngine));
  assert.equal(xtts.engineSetting, 'voice-clone.pythonXtts');
  const qwen = await render('qwen3', { pythonQwen3: forEngine, python: general }, { block: { reference_text: 'a transcript' } });
  assert.ok(same(qwen.exe, forEngine));
  assert.equal(qwen.engineSetting, 'voice-clone.pythonQwen3');
});

test('voice-clone.python is used when the engine has no interpreter of its own', async () => {
  const general = interpreter('py-general-2');
  const ran = await render('xtts', { python: general });
  assert.ok(same(ran.exe, general));
  assert.equal(ran.setting, 'voice-clone.python');
});

test('Chatterbox uses the shared venv under the model root when nothing is set, and an explicit setting still wins', async () => {
  const shared = await render('chatterbox', {});
  assert.ok(same(shared.exe, sharedVenv), shared.exe);
  assert.match(shared.setting, /shared voice-clone venv/);
  const general = interpreter('py-general-3');
  const explicit = await render('chatterbox', { python: general });
  assert.ok(same(explicit.exe, general));
});

test('XTTS-v2 (non-commercial licence) refuses to render until the user has accepted the licence in the settings; the other models need nothing', async () => {
  const refused = await render('xtts', { xttsLicenceAccepted: false }, { expectRefusal: true });
  assert.match(JSON.stringify(refused.error), /non-commercial/);
  const py = interpreter('py-gate');
  const accepted = await render('xtts', { xttsLicenceAccepted: true, pythonXtts: py });
  assert.equal(accepted.engineSetting, 'voice-clone.pythonXtts');
  const other = await render('chatterbox', { xttsLicenceAccepted: false, pythonChatterbox: py });
  assert.equal(other.engineSetting, 'voice-clone.pythonChatterbox');
});
