// ACE-Step plugin: turns an ai-voice block into a request to an acestep.cpp server and brings the audio back as 16-bit mono PCM.
// The server API (POST /lm, POST /synth, GET /job) was checked against ace-server b7ba6d9 (2026-09-24) and run end to end on 2026-10-03; see docs/acestep-plugin.md.
import fs from 'node:fs';
import os from 'node:os';
import crypto from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { serve, wav } from '../sdk/ample-plugin.mjs';
import { toMono16 } from '../sdk/wav.mjs';
import { soundEnd, stretch } from '../sdk/stretch.mjs';
import { runProcess } from './run-process.mjs';

const sleep = ms => new Promise(r => setTimeout(r, ms));
// The synth answer is multipart/mixed with the fixed boundary of ace-server: one audio part (WAV here) followed by its latent part.
function firstAudioPart(buf) {
  const marker = Buffer.from('Content-Type: audio/');
  const at = buf.indexOf(marker);
  if (at < 0) { throw new Error('the ACE-Step answer holds no audio part'); }
  const bodyStart = buf.indexOf('\r\n\r\n', at) + 4;
  const end = buf.indexOf('\r\n--ace-batch-boundary', bodyStart);
  return buf.subarray(bodyStart, end < 0 ? buf.length : end);
}

/** The sha256 of a (large) file, remembered by size and modification time so a model is not read again on every build. */
function sha256Of(file) {
  const stat = fs.statSync(file);
  const cacheFile = path.join(os.tmpdir(), 'ample-model-hashes.json');
  let cache = {};
  try { cache = JSON.parse(fs.readFileSync(cacheFile, 'utf8')); } catch { /* no cache yet */ }
  const key = `${path.resolve(file)}|${stat.size}|${stat.mtimeMs}`;
  if (cache[key]) { return cache[key]; }
  const hash = crypto.createHash('sha256'), fd = fs.openSync(file, 'r'), chunk = Buffer.allocUnsafe(1 << 24);
  try { for (let n; (n = fs.readSync(fd, chunk, 0, chunk.length, null)) > 0;) { hash.update(chunk.subarray(0, n)); } } finally { fs.closeSync(fd); }
  cache[key] = hash.digest('hex');
  try { fs.writeFileSync(cacheFile, JSON.stringify(cache)); } catch { /* the cache is only a speed-up */ }
  return cache[key];
}

/** The sha256 of the adapter as it runs: plugin.mjs and the file it starts processes with (run-process.mjs), so the provenance names both. */
function adapterSha256() {
  const here = path.dirname(fileURLToPath(import.meta.url));
  return crypto.createHash('sha256').update(sha256Of(path.join(here, 'plugin.mjs'))).update(sha256Of(path.join(here, 'run-process.mjs'))).digest('hex');
}

/** How the voice is isolated from the synthesis: ace (an ACE-Step Base or SFT model), demucs (Demucs in a Python of your own) or off. The old boolean vocalExtraction=false still means off. */
const isolationMode = s => s.vocalIsolation || (s.vocalExtraction === false ? 'off' : 'ace');

const isLocal = url => ['localhost', '127.0.0.1', '[::1]', '::1'].includes(new URL(url).hostname);

async function call(url, options, timeoutMs) {
  const headers = { ...(options.headers || {}) };
  if (process.env.ACESTEP_TOKEN) { headers.Authorization = 'Bearer ' + process.env.ACESTEP_TOKEN; }
  let response;
  try { response = await fetch(url, { ...options, headers, signal: AbortSignal.timeout(timeoutMs) }); }
  catch (e) {
    if (e.name === 'TimeoutError' || e.name === 'AbortError') { throw Object.assign(new Error('the ACE-Step server did not answer within ' + Math.round(timeoutMs / 1000) + ' seconds'), { timedOut: true }); }
    throw new Error('the ACE-Step server could not be reached (' + (e.cause && e.cause.code ? e.cause.code : e.name) + ')');
  }
  if (!response.ok) {
    const includeReason = options.method === 'POST' && /\/(?:lm|synth)$/.test(new URL(url).pathname);
    const reason = includeReason ? (await response.text()).trim().slice(0, 2000) : '';
    throw new Error('the ACE-Step server answered HTTP ' + response.status + (reason ? ': ' + reason : ''));
  }
  return response;
}

async function vocalExtractionModel(getProps, settings) {
  // ace-server wants the model's file name exactly as /props lists it, with .gguf (without it the job fails at once).
  if (settings.vocalExtractionModel) { return /\.gguf$/i.test(settings.vocalExtractionModel) ? settings.vocalExtractionModel : `${settings.vocalExtractionModel}.gguf`; }
  const props = await getProps();
  const models = props && props.models && Array.isArray(props.models.dit) ? props.models.dit : [];
  const model = models.find(name => /(?:^|[-_])(base|sft)(?:[-_]|$)/i.test(name));
  if (!model) { throw new Error('AI voice isolation needs an ACE-Step Base or SFT DiT model; install one or select it in the plugin setting vocalExtractionModel'); }
  return model;
}

// The model files ace-server offers for one kind (turbo, sft, base, xl-turbo, xl-sft, xl-base), best first: Q5_K_M is the usual balance, then Q6_K and Q8_0, then the smaller Q4_K_M, then BF16.
const QUANT_ORDER = ['Q5_K_M', 'Q6_K', 'Q8_0', 'Q4_K_M', 'BF16'];
function synthModelFor(props, kind, file, requireListed) {
  const dits = props && props.models && Array.isArray(props.models.dit) ? props.models.dit : [];
  if (file) {
    const name = file.toLowerCase().endsWith('.gguf') ? file : file + '.gguf';
    if (requireListed && !dits.includes(name)) { throw new Error('the ACE-Step CLI models folder has no model file ' + name); }
    return name;
  }
  const prefix = 'acestep-v15-' + kind + '-';
  const found = dits.filter(n => n.startsWith(prefix)).sort((a, b) => QUANT_ORDER.findIndex(q => a.includes(q)) - QUANT_ORDER.findIndex(q => b.includes(q)));
  if (!found.length) { throw new Error('the ACE-Step server has no ' + kind + ' model (it has: ' + dits.join(', ') + ')'); }
  return found[0];
}
// Steps, guidance and shift each kind wants when the block does not say (the server lists the turbo and sft presets in /props).
const KIND_DEFAULTS = { turbo: { steps: 8, guidance: 1, shift: 3 }, sft: { steps: 50, guidance: 7, shift: 1 }, base: { steps: 50, guidance: 7, shift: 3 } };

const exeName = (name) => name + (process.platform === 'win32' ? '.exe' : '');

/** Runs one of the acestep.cpp command line tools; cancel and the time limit stop it. timeoutText is the message for a timeout (it names the stage). */
async function runCli(exe, args, cwd, ctx, timeoutMs, timeoutText) {
  try { await runProcess(exe, args, { cwd, ctx, timeoutMs }); }
  catch (e) {
    if (e.kind === 'timeout') { throw new Error(path.basename(exe) + ' ' + timeoutText); }
    if (e.kind === 'start') { throw new Error('could not start ' + path.basename(exe) + ' (' + e.message + '); check the setting aceBinFolder'); }
    if (e.kind === 'exit') { throw new Error(path.basename(exe) + ' failed (' + e.message + ')'); }
    throw e;
  }
}

/** Runs Demucs (python -m demucs) as an async child. The install hint only appears when Python is missing or reports that the module is missing; a timeout says so. */
async function runDemucs(python, args, cwd, ctx, timeoutMs, timeoutText) {
  try { await runProcess(python, args, { cwd, ctx, timeoutMs }); }
  catch (e) {
    if (e.kind === 'timeout') { throw new Error('Demucs ' + timeoutText); }
    if (e.kind === 'start') { throw new Error('Demucs could not run (' + (e.code === 'ENOENT' ? 'the Python in the setting demucsPython was not found' : e.message) + ')'); }
    if (e.kind === 'exit') { throw new Error('Demucs failed (' + e.message + ')' + (/No module named/i.test(e.stderr || '') ? '; demucs is not installed in that Python: pip install demucs soundfile' : '')); }
    throw e;
  }
}

/** The models the command line backend can use, read from the models folder (the server tells this through /props). */
function cliProps(folder) {
  if (!folder || !fs.existsSync(folder)) { throw new Error('the command line backend needs the setting modelsFolder (the folder with the ACE-Step model files)'); }
  const files = fs.readdirSync(folder).filter(f => f.toLowerCase().endsWith('.gguf'));
  return { models: { dit: files.filter(f => f.startsWith('acestep-v15-')).sort(), lm: files.filter(f => f.startsWith('acestep-5Hz-lm')).sort() } };
}

serve({
  initialize: () => ({ protocol: 1, id: 'acestep', version: '0.6.2', blocks: ['ai-voice', 'ace-music'] }),
  describe: () => ({ toolprint: { plugin: 'acestep', adapter: '1', serverApi: 'ace-server-lm-synth-job' }, determinism: 'unverified' }),
  validate: params => {
    const s = params.settings || {};
    const messages = [];
    if (s.endpoint && !isLocal(s.endpoint) && !s.allowRemote) { messages.push('the endpoint is not local; set allowRemote to use it'); }
    return { ok: messages.length === 0, messages };
  },
  render: async (params, ctx) => {
    const s = params.settings || {};
    const endpoint = (s.endpoint || 'http://127.0.0.1:8085').replace(/\/$/, '');
    if (!isLocal(endpoint) && !s.allowRemote) { throw new Error('the endpoint is not local; the plugin only talks to this machine unless allowRemote is set'); }
    const p = params.block.parameters || {};
    const parameterText = name => (p[name] || '').replaceAll('\\n', '\n');
    const inputText = name => {
      const file = params.inputs && params.inputs[name];
      return file ? fs.readFileSync(path.join(params.jobDir, file), 'utf8') : '';
    };
    const lyrics = inputText('lyrics');
    const style = inputText('style');
    const melody = inputText('melody');
    const isVoice = params.block.type === 'ai-voice';
    if (isVoice && !lyrics.trim()) { throw new Error('AI voice needs linked timed lyrics; open the Lyrics editor and connect its words output'); }
    const wanted = params.barCount * params.samplesPerBar;
    // Meter (#330): beatsPerBar counts quarter notes per bar (missing or invalid: 4). The plugin sends 2, 3 or 4 (2/4, 3/4, 4/4) and refuses everything else before any server or GPU work starts.
    // 6/8 arrives as beatsPerBar 3 (three quarter notes per bar) and is therefore sent as 3/4; 6/4 (6), 5/4 (5) and 7/8 (3.5) are refused.
    const beatsPerBar = Number.isFinite(Number(params.beatsPerBar)) && Number(params.beatsPerBar) > 0 ? Number(params.beatsPerBar) : 4;
    if (![2, 3, 4].includes(beatsPerBar)) { throw new Error('ACE-Step can only follow 2/4, 3/4 and 4/4 (a bar of ' + beatsPerBar + ' quarter notes is not supported); use a project meter with 2, 3 or 4 quarter notes per bar'); }
    // Gaps (#331): the generated sound is one continuous piece, so a block with silent gaps between its bars cannot be honored.
    // The active bars are deduplicated and sorted first (a duplicate such as 3, 3 is not a gap); a real hole is refused before any server request, not partway through the build.
    const rawBars = Array.isArray(params.activeBars) ? params.activeBars.map(Number).filter(Number.isFinite) : [];
    // The block may sit later in the song (at 5 for 4): its sound is made as long as its own bars and placed where the block starts.
    const activeBars = rawBars.length ? [...new Set(rawBars)].sort((a, b) => a - b) : null;
    if (activeBars && activeBars.some((bar, i) => i > 0 && bar !== activeBars[i - 1] + 1)) { throw new Error('ACE-Step makes one continuous region; this block has gaps between its active bars (' + activeBars.join(', ') + '). Use one contiguous block or split it into several blocks'); }
    const activeCount = activeBars ? activeBars.length : params.barCount;
    const offset = activeBars ? Math.max(0, (activeBars[0] - params.firstBar) * params.samplesPerBar) : 0;
    // sing: how many bars the voice sings (ai-voice). ACE fills the length it is given, so the synthesis is made that long and the rest of the block stays silent.
    const singBars = isVoice && Number(String(p.sing || '').replace(/[^0-9.]/g, '')) > 0 ? Math.min(Number(String(p.sing).replace(/[^0-9.]/g, '')), activeCount) : activeCount;
    const sung = Math.round(singBars * params.samplesPerBar);
    // One deadline for the whole render (setup calls, plan, synthesis, extraction, Demucs): every call, wait and child process below is limited by what is left of it.
    const timeoutS = Number.isFinite(Number(s.timeoutS)) && Number(s.timeoutS) > 0 ? Number(s.timeoutS) : 900, timeoutMs = timeoutS * 1000, started = Date.now();
    const timeLeft = () => timeoutMs - (Date.now() - started);
    const timedOut = stage => Object.assign(new Error('the ACE-Step render did not finish within ' + timeoutS + ' seconds (stage: ' + stage + ')'), { deadline: true });
    const remaining = stage => { const left = timeLeft(); if (left <= 0) { throw timedOut(stage); } return left; };
    const cliTimeoutText = stage => 'did not finish within the render timeout of ' + timeoutS + ' seconds (stage: ' + stage + ')';
    const within = (ms, stage) => Math.max(250, Math.min(ms, remaining(stage)));
    const isAbort = e => e && (e.name === 'TimeoutError' || e.name === 'AbortError');
    // A server call that cannot outlive the deadline. read: 'json', 'buffer' or '' (the response).
    const stageFetch = async (stage, url, options, capMs, read, notJson) => {
      const ms = within(capMs, stage);
      try {
        const response = await call(url, options, ms);
        if (read === 'buffer') { return Buffer.from(await response.arrayBuffer()); }
        if (read === 'json') { try { return await response.json(); } catch (e) { throw isAbort(e) ? e : new Error(notJson); } }
        return response;
      } catch (e) {
        if (e.timedOut || isAbort(e)) { throw timeLeft() <= 300 ? timedOut(stage) : new Error('the ACE-Step server did not answer within ' + Math.round(ms / 1000) + ' seconds (stage: ' + stage + ')'); }
        throw e;
      }
    };
    // backend: server (ace-server), cli (ace-lm and ace-synth, run here) or server-then-cli (the server when it answers /health, else the command line tools).
    let backend = s.backend || 'server';
    if (backend === 'server-then-cli') {
      backend = await call(endpoint + '/health', {}, 3000).then(() => 'server', () => 'cli');
      ctx.log('info', 'backend chosen: ' + backend);
    }
    const cli = backend === 'cli';
    if (cli && !s.aceBinFolder) { throw new Error('the command line backend needs the setting aceBinFolder (the folder with ace-lm and ace-synth)'); }
    const getProps = cli ? async () => cliProps(s.modelsFolder) : async () => stageFetch('setup', endpoint + '/props', {}, 30000, 'json', 'the ACE-Step server did not return its model capabilities');
    const kindName = s.synthModel && s.synthModel !== 'default' ? s.synthModel : '';
    const family = /(?:^|-)(sft|base|turbo)(?:-|$)/i.exec(s.synthModelFile || '')?.[1].toLowerCase() || kindName.replace('xl-', '') || 'turbo';
    const synthKind = KIND_DEFAULTS[family] || KIND_DEFAULTS.turbo;
    const guidance = p.guidance === undefined || p.guidance === '' ? synthKind.guidance : Number(p.guidance);
    if (!Number.isFinite(guidance) || guidance < 1 || guidance > 15) throw new Error('CFG guidance must be a finite number from 1 to 15.');
    if (family === 'turbo' && guidance !== 1) throw new Error('Turbo models use CFG 1; select Base/SFT to use classifier-free guidance above 1.');
    const synthModel = s.synthModelFile
      ? synthModelFor(cli ? await getProps() : null, kindName, s.synthModelFile, cli)
      : synthModelFor(await getProps(), kindName || 'turbo', '', true);
    const lmModels = s.lmModelFile || s.cliLmModel ? [] : (await getProps()).models?.lm || [];
    const lmModel = s.lmModelFile || s.cliLmModel || [...lmModels].sort().find(name => name.includes('4B') && name.includes('Q8_0')) || [...lmModels].sort()[0];
    if (!lmModel) throw new Error('Choose an explicit lmModelFile or install an ACE language model advertised by the server.');
    const body = {
      caption: isVoice
        ? ['isolated a cappella lead vocal only', 'no backing music', 'no accompaniment', style, parameterText('voice')].filter(Boolean).join(', ')
        : [s.style || '', style, parameterText('music_direction')].filter(Boolean).join(', '),
      lyrics,
      vocal_language: s.language || 'en',
      seed: Number(BigInt(params.seed) % 4294967296n),
      lm_seed: /^\d+$/.test(p.lm_seed || '') ? Number(p.lm_seed) : Number(BigInt(params.seed) % 4294967296n),
      lm_model: lmModel,
      lm_temperature: p.lm_temperature === undefined || p.lm_temperature === '' ? 0.85 : Number(p.lm_temperature),
      lm_cfg_scale: p.lm_cfg_scale === undefined || p.lm_cfg_scale === '' ? 2 : Number(p.lm_cfg_scale),
      lm_top_p: p.lm_top_p === undefined || p.lm_top_p === '' ? 0.9 : Number(p.lm_top_p),
      lm_top_k: p.lm_top_k === undefined || p.lm_top_k === '' ? 0 : Number(p.lm_top_k),
      lm_negative_prompt: parameterText('lm_negative_prompt'),
      lm_batch_size: 1,
      bpm: Math.round(params.sampleRate * 60 * beatsPerBar / params.samplesPerBar),
      timesignature: String(beatsPerBar),
      inference_steps: Number(p.steps) || synthKind.steps,
      duration: sung / params.sampleRate,
      lm_mode: 'generate',
      use_cot_caption: false,
    };
    // This field is deliberately configurable: ACE-Step server revisions differ in their MIDI conditioning name.
    // A linked melody is therefore a concrete request input, never an ignored graph edge.
    if (melody) { body[s.melodyField || 'melody'] = JSON.parse(melody); }
    // Fail before creating a take if the configured server cannot perform the extraction stage.
    const mode = isVoice ? isolationMode(s) : 'off';
    const isolationModel = mode === 'ace' ? await vocalExtractionModel(getProps, s) : '';
    const json = { 'content-type': 'application/json' };
    // ace-server only says running or done, so the bar moves by an estimate (it slows down towards the end of its span and jumps to the end when the job is done).
    const SPANS = { plan: [0, 0.15, 'plan', 15], synthesis: [0.15, 0.7, 'synthesis', 60], 'vocal extraction': [0.7, 0.97, 'isolation', 40] };
    const waitFor = async (id, stage) => {
      const [from, to, step, tau] = SPANS[stage] || [0, 1, stage, 30], began = Date.now();
      ctx.progress(from, step, null);
      // Best effort and bounded to 5 seconds even when the deadline has passed. 2xx only means the server was asked to stop; the DELETE /job route is not confirmed against ace-server.
      const stopJob = async () => {
        try { await call(endpoint + '/job?id=' + encodeURIComponent(id), { method: 'DELETE' }, 5000); ctx.log('info', 'asked the ACE-Step server to stop job ' + id + ' (the server may or may not honor it)'); }
        catch { ctx.log('warn', 'could not ask the ACE-Step server to stop job ' + id + '; it may still run on the server'); }
      };
      for (;;) {
        if (ctx.isCancelled()) { await stopJob(); throw new Error('cancelled'); }
        if (timeLeft() <= 0) { await stopJob(); throw timedOut(stage); }
        let status;
        try { status = await stageFetch(stage, endpoint + '/job?id=' + encodeURIComponent(id), {}, 30000, 'json', 'the ACE-Step server sent an answer that is not JSON'); }
        catch (e) { if (e.deadline) { await stopJob(); } throw e; }
        if (status.status === 'done') { ctx.progress(to, step, 1); return; }
        if (status.status === 'failed' || status.status === 'cancelled') { throw new Error('the ACE-Step server reported that the ' + stage + ' job ' + status.status + ': ' + JSON.stringify(status)); }
        const guess = 0.9 * (1 - Math.exp(-(Date.now() - began) / 1000 / tau));
        ctx.progress(from + (to - from) * guess, step, guess);
        await sleep(Math.max(0, Math.min(Number(s.pollMs) || 1000, timeLeft())));
      }
    };
    const submit = async (stage, path, payload) => {
      const answer = await stageFetch(stage, endpoint + path, { method: 'POST', headers: json, body: JSON.stringify(payload) }, 30000, 'json', 'the ACE-Step server sent an answer that is not JSON');
      if (!answer.id) { throw new Error('the ACE-Step server did not return a job id'); }
      return answer.id;
    };
    // parts: { audio: the source recording (extract, cover), ref_audio: the voice to sing with }
    const submitWithAudio = async (stage, payload, parts) => {
      const form = new FormData();
      form.append('request', new Blob([JSON.stringify(payload)], { type: 'application/json' }), 'request.json');
      for (const [part, audio] of Object.entries(parts)) { form.append(part, new Blob([audio], { type: 'audio/wav' }), part + '.wav'); }
      const answer = await stageFetch(stage, endpoint + '/synth', { method: 'POST', body: form }, 30000, 'json', 'the ACE-Step server sent an answer that is not JSON');
      if (!answer.id) { throw new Error('the ACE-Step server did not return a job id'); }
      return answer.id;
    };
    // The three steps, by the server or by the command line tools (the same plan and request files, so the same sound).
    const stage = (name) => { const [from, to, step] = SPANS[name]; return { begin: () => ctx.progress(from, step, null), end: () => ctx.progress(to, step, 1) }; };
    const cliDir = path.join(params.jobDir, 'ace');
    const cliTool = name => path.join(s.aceBinFolder || '', exeName(name));
    const planOf = cli ? async (b) => {
      fs.mkdirSync(cliDir, { recursive: true });
      const props = cliProps(s.modelsFolder);
      const lm = b.lm_model || s.cliLmModel || props.models.lm.find(n => n.includes('1.7B')) || props.models.lm[0];
      if (!lm) { throw new Error('no ACE-Step language model (acestep-5Hz-lm-*.gguf) in modelsFolder'); }
      fs.writeFileSync(path.join(cliDir, 'req.json'), JSON.stringify({ ...b, lm_model: lm }));
      const st = stage('plan'); st.begin();
      await runCli(cliTool('ace-lm'), ['--models', s.modelsFolder, '--request', 'req.json'], cliDir, ctx, remaining('plan'), cliTimeoutText('plan'));
      st.end();
      const file = fs.readdirSync(cliDir).filter(n => /^req.+\.json$/.test(n)).sort()[0];
      if (!file) { throw new Error('ace-lm wrote no plan'); }
      const j = JSON.parse(fs.readFileSync(path.join(cliDir, file), 'utf8'));
      return Array.isArray(j) ? j[0] : j;
    } : async (b) => {
      const id = await submit('plan', '/lm', b);
      ctx.log('info', 'plan requested');
      await waitFor(id, 'plan');
      const plans = await stageFetch('plan', endpoint + '/job?id=' + encodeURIComponent(id) + '&result=1', {}, 60000, 'json', 'the ACE-Step server sent a plan that is not JSON');
      return Array.isArray(plans) ? plans[0] : plans;
    };
    const synthOf = cli ? async (r, ref) => {
      fs.writeFileSync(path.join(cliDir, 'synth.json'), JSON.stringify(r));
      if (ref) { fs.writeFileSync(path.join(cliDir, 'ref.wav'), ref); }
      const st = stage('synthesis'); st.begin();
      await runCli(cliTool('ace-synth'), ['--models', s.modelsFolder, '--request', 'synth.json', ...(ref ? ['--ref-audio', 'ref.wav'] : []), ...(ref && r.task_type === 'cover' ? ['--src-audio', 'ref.wav'] : [])], cliDir, ctx, remaining('synthesis'), cliTimeoutText('synthesis'));
      st.end();
      return fs.readFileSync(path.join(cliDir, 'synth.wav'));
    } : async (r, ref) => {
      const id = ref ? await submitWithAudio('synthesis', r, { ref_audio: ref, ...(r.task_type === 'cover' ? { audio: ref } : {}) }) : await submit('synthesis', '/synth', r);
      ctx.log('info', ref ? 'audio requested (voice of the reference)' : 'audio requested');
      await waitFor(id, 'synthesis');
      return firstAudioPart(await stageFetch('synthesis', endpoint + '/job?id=' + encodeURIComponent(id) + '&result=1', {}, 300000, 'buffer'));
    };
    const extractOf = cli ? async (r, source) => {
      fs.writeFileSync(path.join(cliDir, 'source.wav'), source);
      fs.writeFileSync(path.join(cliDir, 'extract.json'), JSON.stringify(r));
      const st = stage('vocal extraction'); st.begin();
      await runCli(cliTool('ace-synth'), ['--models', s.modelsFolder, '--request', 'extract.json', '--src-audio', 'source.wav'], cliDir, ctx, remaining('vocal extraction'), cliTimeoutText('vocal extraction'));
      st.end();
      return fs.readFileSync(path.join(cliDir, 'extract.wav'));
    } : async (r, source) => {
      const id = await submitWithAudio('vocal extraction', r, { audio: source });
      ctx.log('info', 'vocal isolation requested');
      await waitFor(id, 'vocal extraction');
      return firstAudioPart(await stageFetch('vocal extraction', endpoint + '/job?id=' + encodeURIComponent(id) + '&result=1', {}, 300000, 'buffer'));
    };
    const plan = await planOf(body);
    if (!plan) { throw new Error('ACE-Step returned no plan'); }
    const synthRequest = { ...plan, output_format: 'wav16', inference_steps: body.inference_steps };
    Object.assign(synthRequest, { synth_model: synthModel, guidance_scale: guidance, shift: p.shift === undefined || p.shift === '' ? synthKind.shift : Number(p.shift), solver: p.solver || 'euler', synth_batch_size: 1, dcw_scaler: 0, dcw_high_scaler: 0, dcw_mode: 'low', latent_shift: 0, latent_rescale: 1, custom_timesteps: '', adapter: String(p.adapter || '').replaceAll('"', ''), adapter_scale: p.adapter_scale === undefined || p.adapter_scale === '' ? 1 : Number(p.adapter_scale), peak_clip: 10 });
    // reference: the voice (timbre) of a recording; the melody and the words stay those of the plan. ACE takes it as ref_audio next to the request.
    const reference = params.inputs && params.inputs.reference ? fs.readFileSync(path.join(params.jobDir, params.inputs.reference)) : null;
    // voice_strength (optional): the synthesis is also a cover of the reference (task cover) at this strength, the codes of the language model plan are dropped so they do not override the recording
    if (reference && p.voice_strength !== undefined && p.voice_strength !== '') {
      const strength = Number(p.voice_strength);
      if (!Number.isFinite(strength) || strength < 0 || strength > 1) throw new Error('Reference strength must be a number from 0 to 1.');
      Object.assign(synthRequest, { task_type: 'cover', audio_cover_strength: strength });
      delete synthRequest.audio_codes;
    }
    let audio = await synthOf(synthRequest, reference);
    let isolation = 'not-requested', isolationHash = '', extractionRequest = null;
    if (mode === 'demucs') {
      const python = s.demucsPython || 'python', model = s.demucsModel || 'htdemucs';
      const dir = path.join(params.jobDir, 'demucs');
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, 'in.wav'), audio);
      ctx.log('info', 'vocal isolation with demucs');
      const demucsMs = Math.min(1800000, remaining('demucs'));
      await runDemucs(python, ['-m', 'demucs', '--two-stems=vocals', '-n', model, '-d', 'cpu', '-o', path.join(dir, 'out'), path.join(dir, 'in.wav')], dir, ctx, demucsMs, cliTimeoutText('demucs'));
      audio = fs.readFileSync(path.join(dir, 'out', model, 'in', 'vocals.wav'));
      isolation = 'demucs';
      const checkpoints = path.join(os.homedir(), '.cache', 'torch', 'hub', 'checkpoints');
      const found = fs.existsSync(checkpoints) ? fs.readdirSync(checkpoints).find(f => f.startsWith(model + '-')) : '';
      isolationHash = found ? sha256Of(path.join(checkpoints, found)) : '';
    } else if (mode === 'ace') {
      const extraction = {
        caption: 'isolated a cappella vocals only',
        lyrics: '',
        task_type: 'extract',
        track: 'vocals',
        synth_model: isolationModel,
        output_format: 'wav16',
        inference_steps: Number(s.vocalExtractionSteps || 50),
        seed: body.seed,
        guidance_scale: 7,
        shift: 1,
        solver: 'euler',
      };
      extractionRequest = extraction;
      audio = await extractOf(extraction, audio);
      isolation = 'extract';
      if (s.modelsFolder && fs.existsSync(path.join(s.modelsFolder, isolationModel))) { isolationHash = sha256Of(path.join(s.modelsFolder, isolationModel)); }
    }
    let samples = toMono16(audio, params.sampleRate, wanted, sung);
    let stretched = 1;
    // fit stretch: ACE tends to sing a little faster than the music and then runs out. The voice is slowed (the pitch stays) so that it ends where the lyrics end (the end of the sing length).
    if (isVoice && p.fit === 'stretch') {
      const end = soundEnd(samples.subarray(0, sung)), target = Math.round(sung * 0.97);
      if (end > 0 && end < target * 0.97) {
        stretched = Math.min(1.6, target / end);
        const slow = stretch(samples.subarray(0, end), stretched, params.sampleRate);
        const fitted = new Int16Array(wanted);
        fitted.set(slow.subarray(0, Math.min(slow.length, sung)));
        const fade = Math.min(Math.round(params.sampleRate * 0.08), Math.min(slow.length, sung));
        for (let i = 0; i < fade; i++) { const at = Math.min(slow.length, sung) - fade + i; fitted[at] = Math.round(fitted[at] * (1 - (i + 1) / fade)); }
        samples = fitted;
      }
    }
    if (offset > 0) { const placed = new Int16Array(wanted); placed.set(samples.subarray(0, Math.max(0, wanted - offset)), offset); samples = placed; }
    fs.mkdirSync(path.join(params.jobDir, 'out'), { recursive: true });
    const output = isVoice ? 'vocal.wav' : 'audio.wav';
    fs.writeFileSync(path.join(params.jobDir, 'out', output), wav(samples, params.sampleRate));
    const modelHashes = Object.fromEntries([synthModel, isolation === 'extract' ? isolationModel : '', plan.lm_model, plan.vae_model, plan.text_encoder_model].filter(name => typeof name === 'string' && name && s.modelsFolder && fs.existsSync(path.join(s.modelsFolder, name))).map(name => [name, sha256Of(path.join(s.modelsFolder, name))]));
    const engine = s.aceBinFolder && path.join(s.aceBinFolder, exeName(cli ? 'ace-synth' : 'ace-server'));
    const recipe = { schema: 1, block: params.block, seed: params.seed, sampleRate: params.sampleRate, samplesPerBar: params.samplesPerBar, firstBar: params.firstBar, barCount: params.barCount, activeBars: params.activeBars, planRequest: body, plan, synthRequest, extractionRequest, modelHashes, software: { adapterSha256: adapterSha256(), engineSha256: engine && fs.existsSync(engine) ? sha256Of(engine) : null, node: process.version, platform: process.platform, arch: process.arch }, warnings: [!modelHashes[synthModel] ? 'Synthesis model hash unavailable: exact model identity is unverified.' : '', !engine || !fs.existsSync(engine) ? 'ACE executable hash unavailable: engine identity is unverified.' : ''].filter(Boolean) };
    return { outputs: [{ role: isVoice ? 'vocal' : 'audio', path: `out/${output}` }], meta: { melodyPlan: melody ? 'requested' : 'none', mode: isVoice ? 'vocal-only' : 'music', vocalIsolation: isolation, vocalIsolationModel: isolation === 'extract' ? isolationModel : isolation === 'demucs' ? (s.demucsModel || 'htdemucs') : 'none', backend, synthModel, guidance, recipe, vocalIsolationSha256: isolationHash || 'unknown', voiceStretch: Math.round(stretched * 1000) / 1000, seconds: Math.round((Date.now() - started) / 100) / 10 } };
  },
});
