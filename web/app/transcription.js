import { registerPlaybackAudio } from './playback-volume.js';
// Project-scoped audio separation and vocal pitch review. @ample:audio-transcription

import { api } from './api.js';
import { $, ask, el, toast } from './ui.js';
import { S, edit, notify, readOnly, select } from './store.js';
import { encodeVocalMidi, notesFromPitch } from '../lib/vocal-midi.js';

const T = { state: 'idle', result: null, progress: null, error: '', abort: null, events: null, audio: new Map(), peaks: null, notes: [], playing: false, remote: false };

export async function openTranscription() {
  if (T.state === 'running') { toast('An audio analysis is already running.'); return; }
  const settings = await api.get('/api/project/settings');
  const remote = settings.faer?.separation === 'faer';
  const list = remote ? [] : (await api.get('/api/plugins')).plugins || [];
  const plugins = remote ? [{ id: 'faer', title: 'Faer (remote GPU) stem separation', needs: {} }] : list.filter((plugin) => plugin.kind === 'analysis' && plugin.analyses?.includes('transcription') && !plugin.needs?.network);
  if (!plugins.length) { toast('Install a local analysis plugin that offers transcription, then try again.'); return; }
  if (remote) { const status = await api.get('/api/faer/status'); if (!status.connected || !status.status?.capabilities?.separation?.ready) { toast('Faer (remote GPU) stem separation is unavailable. Check Project settings.'); return; } }
  const file = el('input', { type: 'file', accept: '.wav,audio/wav', style: 'display:block;margin:8px 0' });
  const select = el('select', { style: 'display:block;min-width:260px;margin:8px 0' });
  plugins.forEach((plugin) => select.append(el('option', { value: plugin.id, textContent: `${plugin.title || plugin.id} (${plugin.id})` })));
  const pluginOf = () => plugins.find((plugin) => plugin.id === select.value);
  const device = el('select', { style: 'display:block;min-width:180px;margin:8px 0' }, [el('option', { value: 'gpu', textContent: 'GPU (default)' }), el('option', { value: 'cpu', textContent: 'CPU' })]);
  const deviceLabel = el('label', { style: 'display:block;margin:10px 0' }, ['Compute device', device]);
  const options = el('textarea', { rows: 3, value: '{}', style: 'width:100%;box-sizing:border-box;font-family:monospace' });
  const gpu = el('input', { type: 'checkbox', style: 'width:auto;margin-right:8px' });
  const gpuLabel = el('label', { style: 'display:block;margin:8px 0' }, [gpu, 'Allow this job to use the GPU']);
  const updateDevice = () => { const local = pluginOf()?.id === 'analysis-local-transcription'; deviceLabel.hidden = !local; gpuLabel.hidden = !pluginOf()?.needs?.gpu || (local && device.value === 'cpu'); gpu.checked = false; };
  select.onchange = updateDevice; device.onchange = updateDevice; updateDevice();
  const extras = [
    el('label', {}, ['WAV audio (maximum 10 minutes, 128 MiB)', file]),
    el('label', { style: 'display:block;margin:10px 0' }, [remote ? 'Analysis route' : 'Local analysis plugin', select]),
    deviceLabel,
    ...(!remote ? [el('label', { style: 'display:block;margin:10px 0' }, ['Plugin options (JSON object)', options])] : []),
    gpuLabel,
  ];
  if (await ask('Separate and transcribe audio', remote ? 'Faer receives only this audio as lossless FLAC when smaller, then returns aligned WAV stems. Results are estimates; review stems before using them.' : 'The chosen local plugin receives this WAV. Network plugins are excluded. Results are estimates; review stems and pitch before converting them to MIDI.', [{ label: 'Cancel', value: false }, { label: remote ? 'Run on Faer (remote GPU)' : 'Run locally', value: true, primary: true }], extras) !== true) return;
  if (!file.files?.[0]) { toast('Choose a WAV file first.'); return; }
  if (file.files[0].size > 128 * 1024 * 1024) { toast('The WAV file exceeds the 128 MiB limit.'); return; }
  let parsedOptions;
  try { parsedOptions = JSON.parse(options.value || '{}'); } catch { toast('Plugin options must be valid JSON.'); return; }
  if (!parsedOptions || Array.isArray(parsedOptions) || typeof parsedOptions !== 'object') { toast('Plugin options must be a JSON object.'); return; }
  if (pluginOf()?.id === 'analysis-local-transcription') parsedOptions.computeDevice = device.value;
  await run(file.files[0], pluginOf(), parsedOptions, gpu.checked);
}

async function run(file, plugin, options, allowGpu) {
  stopTranscription();
  for (const audio of T.audio.values()) { audio.pause(); audio.src = ''; }
  T.audio.clear(); T.result = null; T.state = 'running'; T.error = ''; T.progress = null; T.remote = plugin.id === 'faer';
  T.abort = new AbortController();
  const job = crypto.randomUUID().replaceAll('-', '');
  T.events = new EventSource('/api/events');
  T.events.addEventListener('analysis.progress', (event) => {
    const progress = JSON.parse(event.data);
    if (progress.job === job) { T.progress = progress; notify(); }
  });
  notify();
  try {
    T.result = await api.transcribeAudio(file, plugin.id, options, job, allowGpu, T.abort.signal);
    T.state = 'ready';
    T.notes = T.result.result.vocalPitch ? notesFromPitch(T.result.result.vocalPitch.frames, T.result.result.vocalPitch.hopSeconds) : [];
    try { T.peaks = await api.transcriptionPeaks(T.result.id, 'source'); } catch { T.peaks = null; }
    for (const stem of T.result.stems.filter((item) => item.contentType === 'audio/wav')) {
      const audio = registerPlaybackAudio(new Audio(stem.url)); audio.preload = 'auto'; T.audio.set(stem.role, audio);
    }
  } catch (error) {
    T.state = error.name === 'AbortError' ? 'cancelled' : 'failed';
    T.error = error.name === 'AbortError' ? 'Analysis cancelled.' : error.message;
  } finally {
    T.abort = null; T.events?.close(); T.events = null; notify();
  }
}

export function stopTranscription() {
  if (T.state === 'running') T.abort?.abort();
  for (const audio of T.audio.values()) audio.pause();
  T.playing = false;
  notify();
}

const setTime = (seconds) => {
  for (const audio of T.audio.values()) { audio.currentTime = seconds; }
};

function drawPitch(canvas, frames, duration) {
  const ctx = canvas.getContext('2d'); const width = canvas.width, height = canvas.height;
  const colors = getComputedStyle(document.documentElement);
  ctx.clearRect(0, 0, width, height);
  if (T.peaks?.peaks?.length) {
    const pairs = T.peaks.peaks; ctx.fillStyle = colors.getPropertyValue('--panel3').trim(); ctx.beginPath();
    for (let i = 0; i < pairs.length; i += 2) { const x = (i / pairs.length) * width, mid = height / 2; ctx.fillRect(x, mid - pairs[i + 1] * height / 3, Math.max(1, width * 2 / pairs.length), (pairs[i + 1] - pairs[i]) * height / 3); }
  }
  ctx.strokeStyle = colors.getPropertyValue('--line-strong').trim(); ctx.lineWidth = 1;
  for (let note = 36; note <= 84; note += 12) { const y = height - ((note - 36) / 48) * height; ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(width, y); ctx.stroke(); }
  for (const frame of frames) {
    if (!(frame.hz > 0) || !Number.isFinite(frame.t) || frame.t < 0 || frame.t > duration) continue;
    const midi = frame.midi, x = Math.round((frame.t / Math.max(duration, 0.001)) * width), y = height - ((midi - 36) / 48) * height;
    if (y < 0 || y > height) continue;
    ctx.globalAlpha = Math.max(0.15, Math.min(1, frame.confidence)); ctx.fillStyle = colors.getPropertyValue('--ok').trim(); ctx.fillRect(x, y - 2, 3, 4);
  }
  ctx.globalAlpha = 1;
}

function reviewPanel() {
  const result = T.result.result, duration = result.sourceSamples / result.sampleRate;
  const box = el('section', { className: 'note', style: 'margin:12px 0;padding:12px;border:1px solid var(--line);border-radius:8px' });
  box.append(el('h3', { textContent: 'Audio analysis review', style: 'margin:0 0 6px' }));
  const provenance = `Tool: ${result.tool} ${result.toolVersion} · Model: ${result.model}${result.modelLicense ? ` · License entered: ${result.modelLicense}` : ''}${result.modelHash ? ` · SHA-256: ${result.modelHash}` : ''} · ${duration.toFixed(1)} seconds. Treat MIDI and pitch as estimates.`;
  box.append(el('p', { textContent: provenance, style: 'margin:4px 0 10px;overflow-wrap:anywhere' }));
  const audioItems = T.result.stems.filter((stem) => stem.contentType === 'audio/wav' && T.audio.has(stem.role));
  const controls = el('div', { className: 'row' });
  const play = el('button', { textContent: T.playing ? 'Pause stems' : 'Play stems', className: 'primary' });
  play.onclick = async () => {
    if (T.playing) stopTranscription();
    else {
      const now = T.audio.get('source')?.currentTime || 0;
      const started = await Promise.all([...T.audio.values()].map((audio) => { audio.currentTime = now; return audio.play().then(() => true).catch(() => false); }));
      T.playing = started.some(Boolean);
      if (!T.playing) toast('The audio could not be played by this browser.');
    }
    notify();
  };
  const seek = el('input', { type: 'range', min: 0, max: duration, step: 0.01, value: T.audio.get('source')?.currentTime || 0, style: 'flex:1;min-width:160px' });
  seek.oninput = () => setTime(Number(seek.value));
  const clock = el('span', { textContent: `${(T.audio.get('source')?.currentTime || 0).toFixed(1)} / ${duration.toFixed(1)} s` });
  for (const audio of T.audio.values()) audio.ontimeupdate = () => { const time = T.audio.get('source')?.currentTime || 0; seek.value = String(time); clock.textContent = `${time.toFixed(1)} / ${duration.toFixed(1)} s`; };
  controls.append(play, seek, clock);
  box.append(controls);
  const tracks = el('div', { className: 'row', style: 'gap:12px;flex-wrap:wrap' });
  audioItems.forEach((stem) => {
    const mute = el('input', { type: 'checkbox', checked: true, style: 'width:auto;margin-right:5px' });
    mute.onchange = () => { const audio = T.audio.get(stem.role); if (audio) audio.volume = mute.checked ? 1 : 0; };
    tracks.append(el('label', { style: 'white-space:nowrap' }, [mute, stem.role]));
  });
  box.append(tracks);
  const frames = result.vocalPitch?.frames || [];
  box.append(el('h4', { textContent: 'Vocal pitch and model activity', style: 'margin:12px 0 4px' }));
  if (frames.length) {
    const canvas = el('canvas', { width: 960, height: 180, style: 'width:100%;height:180px;background:var(--panel2);border-radius:4px', 'aria-label': 'Vocal pitch lane; vertical position is MIDI pitch and opacity represents clamped model activity, not calibrated confidence' });
    drawPitch(canvas, frames, duration); box.append(canvas);
  } else box.append(el('p', { textContent: 'This plugin did not return a vocal pitch contour.' }));
  if (T.notes.length) {
    box.append(el('h4', { textContent: `Review and correct vocal notes (${T.notes.length})`, style: 'margin:12px 0 4px' }));
    const table = el('table', { style: 'width:100%;border-collapse:collapse' });
    table.append(el('tr', {}, ['Start (s)', 'Length (s)', 'MIDI pitch', 'Model activity (0–1)'].map((label) => el('th', { textContent: label }))));
    T.notes.forEach((note, index) => {
      const start = el('input', { type: 'number', min: '0', step: '0.01', value: (note.startTick / 960).toFixed(2), style: 'width:90px' });
      const length = el('input', { type: 'number', min: '0.01', step: '0.01', value: ((note.endTick - note.startTick) / 960).toFixed(2), style: 'width:90px' });
      const pitch = el('input', { type: 'number', min: '0', max: '127', step: '1', value: note.pitch, style: 'width:72px' });
      start.onchange = () => { note.startTick = Math.max(0, Math.round(Number(start.value) * 960)); note.endTick = Math.max(note.startTick + 1, note.endTick); };
      length.onchange = () => { note.endTick = note.startTick + Math.max(1, Math.round(Number(length.value) * 960)); };
      pitch.onchange = () => { note.pitch = Math.max(0, Math.min(127, Math.round(Number(pitch.value)))); };
      table.append(el('tr', {}, [el('td', {}, [start]), el('td', {}, [length]), el('td', {}, [pitch]), el('td', { textContent: note.confidence.toFixed(2) })]));
      table.lastElementChild.dataset.note = String(index);
    });
    box.append(table);
    const actions = el('div', { className: 'row' });
    actions.append(el('button', { textContent: 'Download reviewed vocal MIDI', onclick: () => downloadNotes() }));
    actions.append(el('button', { textContent: 'Import reviewed melody as MIDI block', className: 'primary', disabled: readOnly(), onclick: () => importNotes().catch((error) => toast(error.message)) }));
    box.append(actions);
  }
  if (result.warnings?.length) result.warnings.forEach((warning) => box.append(el('p', { textContent: warning, className: 'note' })));
  const midi = T.result.stems.filter((stem) => stem.contentType === 'audio/midi');
  if (midi.length) {
    box.append(el('h4', { textContent: 'MIDI estimates (review before importing)' }));
    midi.forEach((item) => {
      const row = el('div', { className: 'row' });
      row.append(el('a', { href: item.url, download: '', textContent: `Download ${item.role}.mid` }));
      if (item.role === 'vocal-midi') row.append(el('span', { textContent: 'Review and accept notes in the pitch lane before importing vocal MIDI.' }));
      else row.append(el('button', { textContent: `Import ${item.role} as MIDI block`, disabled: readOnly(), onclick: () => importArtifact(item).catch((error) => toast(error.message)) }));
      box.append(row);
    });
  }
  else box.append(el('p', { textContent: 'No MIDI artifact was returned. Configure the local transcription plugin to return reviewed backing or vocal MIDI.' }));
  return box;
}

function midiBytes() {
  for (const row of document.querySelectorAll('#b-arr [data-note]')) {
    const index = Number(row.dataset.note), note = T.notes[index], inputs = row.querySelectorAll('input');
    if (!note || inputs.length !== 3) continue;
    note.startTick = Math.max(0, Math.round(Number(inputs[0].value) * 960));
    note.endTick = Math.max(note.startTick + 1, note.startTick + Math.round(Number(inputs[1].value) * 960));
    note.pitch = Math.max(0, Math.min(127, Math.round(Number(inputs[2].value))));
  }
  return encodeVocalMidi(T.notes);
}

function downloadNotes() {
  try {
    const url = URL.createObjectURL(new Blob([midiBytes()], { type: 'audio/midi' }));
    const anchor = el('a', { href: url, download: 'reviewed-vocals.mid' }); document.body.append(anchor); anchor.click(); anchor.remove(); setTimeout(() => URL.revokeObjectURL(url), 0);
  } catch (error) { toast(error.message); }
}

async function importNotes() {
  if (!T.notes.length || !S.doc) { toast('There are no reviewed vocal notes to import.'); return; }
  const file = new File([midiBytes()], 'reviewed-vocals.mid', { type: 'audio/midi' });
  await addMidiBlock(file, 'reviewed_vocal');
}

async function importArtifact(item) {
  const response = await fetch(item.url);
  if (!response.ok) throw new Error(`MIDI download failed (${response.status}).`);
  const file = new File([await response.blob()], `${item.role}.mid`, { type: 'audio/midi' });
  await addMidiBlock(file, item.role);
}

async function addMidiBlock(file, label) {
  if (!S.doc) throw new Error('Open a song before importing MIDI.');
  const imported = await api.upload('/api/midi/upload', file, '.mid', 'all');
  const blocks = S.moduleStack.length ? S.moduleDoc.blocks : S.doc.blocks;
  const base = label.toLowerCase().replace(/[^a-z0-9_]+/g, '_').replace(/^_+|_+$/g, '') || 'transcribed_midi';
  let name = base, suffix = 2;
  while (blocks.some((block) => block.name === name)) name = `${base}_${suffix++}`;
  const ok = await edit([
    { type: 'addBlock', blockType: 'midi', name },
    { type: 'setParameter', block: name, name: 'file', value: imported.path },
    { type: 'setParameter', block: name, name: 'track', value: 'all' },
    { type: 'setParameter', block: name, name: 'bars', value: String(imported.bars) },
    { type: 'setLayout', block: name, x: 160, y: 100 + blocks.length * 24 },
  ]);
  if (ok) { select(name); notify(); toast(`Imported ${name}. Open the MIDI block to review its editable track module.`); }
}

export function renderTranscriptionPanel() {
  if (T.state === 'idle') return null;
  const box = el('section', { className: 'note', style: 'margin:12px 0;padding:12px;border:1px solid var(--line);border-radius:8px' });
  if (T.state === 'running') {
    const step = T.progress?.step ? ` · ${T.progress.step}` : '';
    box.append(el('span', { textContent: `Running ${T.remote ? 'on Faer (remote GPU)' : 'local analysis'}${step}` }));
    if (Number.isFinite(T.progress?.fraction)) box.append(el('progress', { max: 1, value: T.progress.fraction, style: 'margin:0 12px;vertical-align:middle' }));
    box.append(el('button', { textContent: 'Cancel', onclick: () => T.abort?.abort() }));
  } else if (T.state === 'ready') return reviewPanel();
  else box.append(el('span', { textContent: T.error }), el('button', { textContent: 'Dismiss', onclick: () => { T.state = 'idle'; notify(); } }));
  return box;
}
