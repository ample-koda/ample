// node --test plugins/acestep/plugin.test.mjs: drives plugin.mjs over its protocol against stub-server.mjs; covers cancel (DELETE /job) without the host, which stops the plugin right after it sends cancel.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

async function stub(mode) {
  const child = spawn(process.execPath, ['stub-server.mjs', mode], { cwd: here, stdio: ['ignore', 'pipe', 'inherit'] });
  const line = await new Promise(resolve => readline.createInterface({ input: child.stdout }).once('line', resolve));
  return { child, endpoint: 'http://127.0.0.1:' + line.slice(5) };
}

// Renders one ai-voice block and cancels it after cancelAfterMs; resolves { error, logs }.
async function renderAndCancel(endpoint, cancelAfterMs) {
  const jobDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ample-ace-cancel-'));
  fs.mkdirSync(path.join(jobDir, 'in'));
  fs.writeFileSync(path.join(jobDir, 'in', 'lyrics.bin'), 'la la');
  const plugin = spawn(process.execPath, ['plugin.mjs'], { cwd: here, stdio: ['pipe', 'pipe', 'inherit'] });
  const logs = [];
  const answer = new Promise(resolve => readline.createInterface({ input: plugin.stdout }).on('line', line => {
    const msg = JSON.parse(line);
    if (msg.method === 'log') { logs.push(msg.params.level + ': ' + msg.params.message); }
    if (msg.id === 1 && (msg.result || msg.error)) { resolve(msg.error ? msg.error.message : 'ok'); }
  }));
  const send = o => plugin.stdin.write(JSON.stringify({ jsonrpc: '2.0', ...o }) + '\n');
  send({ id: 1, method: 'render', params: { jobDir, block: { type: 'ai-voice', name: 'v', parameters: {} }, seed: '1', sampleRate: 8000, samplesPerBar: 4000, firstBar: 1, barCount: 2, activeBars: [1, 2], inputs: { lyrics: 'in/lyrics.bin' }, settings: { endpoint, pollMs: 50 } } });
  setTimeout(() => send({ method: 'cancel', params: { id: 1 } }), cancelAfterMs);
  const error = await answer;
  plugin.kill();
  return { error, logs };
}

test('cancel asks the server to stop the running job and says so honestly', async () => {
  const s = await stub('neverdone');
  try {
    const { error, logs } = await renderAndCancel(s.endpoint, 700);
    assert.equal(error, 'cancelled');
    const cancelled = await (await fetch(s.endpoint + '/cancelled')).json();
    assert.equal(cancelled.length, 1);
    assert.ok(logs.some(l => l.startsWith('info: asked the ACE-Step server to stop job')), logs.join('\n'));
  } finally { s.child.kill(); }
});

test('when the server has no DELETE route the log says the job may still run', async () => {
  const s = await stub('nodelete');
  try {
    // nodelete answers like neverdone otherwise only for DELETE; make the job stay running through a status that never finishes
    const { error, logs } = await renderAndCancel(s.endpoint, 100);
    assert.equal(error, 'cancelled');
    assert.ok(logs.some(l => l.startsWith('warn: could not ask the ACE-Step server to stop job') && l.includes('may still run')), logs.join('\n'));
    assert.equal((await (await fetch(s.endpoint + '/cancelled')).json()).length, 0);
  } finally { s.child.kill(); }
});
