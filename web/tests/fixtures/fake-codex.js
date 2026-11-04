// A stand-in for the Codex app-server, for the specs: it answers the JSON-RPC calls AMPLE makes and "does" one task (adds a comment line to
// project/song.amplet of its working folder), so the proposal flow can be tested without a Codex login or a model.
import fs from 'node:fs';
import path from 'node:path';
if (process.argv.includes('--version')) { console.log('codex-cli 0.0.0-fake'); process.exit(0); }
let buffer = '';
const send = (o) => process.stdout.write(JSON.stringify(o) + '\n');
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  buffer += chunk;
  let i;
  while ((i = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, i).trim();
    buffer = buffer.slice(i + 1);
    if (line) handle(JSON.parse(line));
  }
});
function handle(m) {
  if (m.id === undefined) return;   // a notification such as "initialized"
  const reply = (result) => send({ id: m.id, result });
  switch (m.method) {
    case 'initialize': return reply({ userAgent: 'fake-codex' });
    case 'account/read': return reply({ account: { type: 'chatgpt', email: ['fake', 'example.test'].join(String.fromCharCode(64)), planType: 'plus' }, requiresOpenaiAuth: false });
    case 'thread/start': return reply({ thread: { id: 'thread-fake' } });
    case 'thread/resume': return reply({ thread: { id: (m.params && m.params.threadId) || 'thread-fake' } });
    case 'turn/steer': return reply({ turnId: 'turn-fake' });
    case 'turn/start': {
      reply({ turn: { id: 'turn-fake', status: 'inProgress' } });
      { const text = String(m.params?.input?.[0]?.text || ''); setTimeout(() => { work(text).catch((e) => { send({ method: 'item/completed', params: { item: { type: 'agentMessage', text: 'fake Codex failed: ' + e.message } } }); send({ method: 'turn/completed', params: { turn: { id: 'turn-fake', status: 'completed' } } }); }); }, text.includes('SLOW') ? 5000 : 200); }
      return undefined;
    }
    default: return reply({});
  }
}
async function work(prompt) {
  const file = path.join(process.cwd(), 'project', 'song.amplet');
  const persistBuildFail = path.join(process.cwd(), '.persist-buildfail');
  if (prompt.includes('PERSIST_BUILDFAIL')) fs.writeFileSync(persistBuildFail, '');
  const keepBuildFailure = fs.existsSync(persistBuildFail);
  const text = fs.readFileSync(file, 'utf8');
  if (prompt.includes('NEW_SONG')) fs.writeFileSync(path.join(process.cwd(), 'project', 'first-bell.amplet'), text + '\n# a new song from the fake Codex\n');
  const lines = text.split('\n');
  const at = Math.max(1, lines.findIndex((l) => /^\s*(drums|bass|piano|mixer|master|midi-clip)\b/.test(l)));
  if (prompt.includes('does not build')) {
    // AMPLE sent it back with the details of the failed build: it takes the broken block out
    const i = lines.findIndex((l) => l.startsWith('import-audio ghost'));
    if (i >= 0 && !keepBuildFailure) lines.splice(i, 3);
  } else if (prompt.includes('found problems')) {
    // AMPLE sent it back: it removes the line it broke and the block that cannot work
    for (let i = lines.length - 1; i >= 0; i--) if (lines[i].includes('NOT AMPLE')) lines.splice(i, 1);
    const g = lines.findIndex((l) => l.startsWith('import-audio ghost'));
    if (g >= 0 && !keepBuildFailure) lines.splice(g, 3);
  } else {
    lines.splice(at, 0, '# a comment from the fake Codex');
    if (prompt.includes('BREAK')) lines.splice(at, 0, 'THIS IS NOT AMPLE');
    const offered = /samples\/offered\/[\w.-]+\.wav/.exec(prompt);
    if (prompt.includes('USE_OFFERED') && offered) lines.push('import-audio offeredsound at 1 for 1 {', '  file ' + offered[0], '}');   // uses the first offered sound
    if (prompt.includes('BUILDFAIL') && !prompt.includes('PERSIST_BUILDFAIL')) {
      const insertAt = lines.findIndex((line) => /^\s*master\b/.test(line));
      lines.splice(insertAt >= 0 ? insertAt : lines.length, 0, ...(keepBuildFailure ? ['sequencer ghost at 1 for 2 {', '}'] : ['import-audio ghost at 1 for 2 {', '  file samples/does-not-exist.wav', '}']));   // an empty sequencer parses but fails when rendered
    }
  }
  fs.writeFileSync(file, lines.join('\n'));
  const presented = await presentClips(prompt);
  send({ method: 'item/completed', params: { item: { type: 'agentMessage', text: 'Added one comment line (fake Codex).' + (presented.length ? ' PRESENTED ' + JSON.stringify(presented) : '') + (prompt.includes('ECHO_PROMPT') ? ' PROMPT_BEGIN ' + prompt + ' PROMPT_END' : '') } } });
  send({ method: 'turn/completed', params: { turn: { id: 'turn-fake', status: 'completed' } } });
}

// "PRESENT_<n>" in the prompt: the fake calls the task-scoped present route n times, like the MCP tool ample_present does (the token and the address
// come from the -c mcp_servers.ample=... argument AMPLE gave this process). "PRESENT_CURRENT" presents the current project instead of the proposal,
// "BLOCK_<name>" picks the block (default mix), "TRIAL_<n>" tries the mixer gains n n n 0 as a temporary edit, "ECHO_PROMPT" sends the prompt back in the answer.
async function presentClips(prompt) {
  const last = (re) => [...prompt.matchAll(re)].pop();   // the newest instruction wins: a continuation quotes the earlier request first
  const count = last(/PRESENT_(\d+)/g);
  if (!count) return [];
  const argv = process.argv.join(' ');
  const token = /AMPLE_CONTEXT_TOKEN=\\?"([^"\\]+)/.exec(argv)?.[1];
  const url = /"--url\\?",\\?"([^"\\]+)/.exec(argv)?.[1];
  if (!token || !url) throw new Error('no context token or address in the arguments');
  const block = last(/BLOCK_([\w-]+)/g)?.[1] || 'mix';
  const trial = last(/TRIAL_(-?\d+)/g)?.[1];
  const source = prompt.includes('PRESENT_CURRENT') ? 'current' : 'proposal';
  const out = [];
  for (let i = 0; i < Number(count[1]); i++) {
    const response = await fetch(url + '/api/ai/context/present', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Context': token }, body: JSON.stringify({ block, from: 1, to: 2, unit: 'bars', label: 'fake clip ' + (i + 1), why: 'fake', source, ...(trial === undefined ? {} : { edits: [{ op: 'set_param', block: 'mix', name: 'gains', value: trial + ' ' + trial + ' ' + trial + ' 0' }] }) }) });
    const body = await response.json().catch(() => ({}));
    out.push({ status: response.status, presented: body.presented ?? null, limitReached: body.limitReached ?? null, error: body.error?.code ?? body.code ?? null, hash: body.clip?.audioHash ?? body.audioHash ?? null, identicalTo: body.clip?.identicalTo ?? body.identicalTo ?? null, trial: body.trial ?? null, note: String(body.error?.message ?? body.message ?? body.note ?? '').slice(0, 160) });
  }
  return out;
}
