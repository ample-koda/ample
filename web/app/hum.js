import { registerPlaybackAudio, playbackOutput } from './playback-volume.js';
// Hum a melody: record a hummed or sung line, let AMPLE find the notes in it (an attempt), listen to both and correct the settings, and only then hand the melody on:
// to Codex (with the raw audio), into the piano roll or as a new midi-clip. @ample:hum

import { el, toast } from './ui.js';
import { api } from './api.js';
import { S } from './store.js';
import { openRecorder } from './recorder.js';

const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
export const noteName = (pitch) => `${NAMES[((pitch % 12) + 12) % 12]}${Math.floor(pitch / 12) - 1}`;

/** Asks AMPLE for the melody in a WAV (bytes): { notes, notesText, key, quality, ... }. */
export const transcribe = (bytes, { tempo, grid, key, bar } = {}) => api.postBinary(`/api/hum/transcribe?tempo=${tempo || 120}&grid=${grid ?? 0.25}&key=${encodeURIComponent(key || '')}&bar=${bar || 1}`, bytes);

/** What the song says about its tempo. */
export const songTempo = () => Number(S.doc?.project?.tempo) || 120;

let playing = null;
function playNotes(notes, tempo) {
  stopHearing();
  const context = new AudioContext();
  const start = context.currentTime + 0.1;
  const beat = 60 / (tempo || 120);
  for (const n of notes) {
    const o = context.createOscillator(), g = context.createGain();
    o.type = 'triangle';
    o.frequency.value = 440 * 2 ** ((n.pitch - 69) / 12);
    const at = start + n.start, end = at + Math.max(0.08, n.duration);
    g.gain.setValueAtTime(0, at); g.gain.linearRampToValueAtTime(0.25, at + 0.02); g.gain.setValueAtTime(0.25, end - 0.04); g.gain.linearRampToValueAtTime(0, end);
    o.connect(g).connect(playbackOutput(context));
    o.start(at); o.stop(end + 0.02);
  }

  playing = { context, beat };
  const last = notes.length ? notes[notes.length - 1] : { start: 0, duration: 0 };
  setTimeout(() => { if (playing?.context === context) stopHearing(); }, (last.start + last.duration + 0.5) * 1000);
}
function stopHearing() { playing?.context.close().catch(() => {}); playing = null; }

/**
 * Records a hum (the recorder with a count-in at the tempo of the song) and then shows what AMPLE found: the notes on a small roll, with the settings to change it.
 * context: { tempo, bar, title, acceptLabel, onAccept({ result, bytes, options }) }
 */
export async function startHum(context = {}) {
  const tempo = context.tempo || songTempo();
  await openRecorder({
    title: context.title || 'Hum a melody',
    hum: true,
    tempo,
    useLabel: 'Find the melody',
    onUse: async (bytes) => { await reviewHum(bytes, { ...context, tempo }); },
  });
}

export async function reviewHum(bytes, context) {
  const state = { tempo: context.tempo, grid: 0.25, key: '', bar: context.bar || 1, result: null, bytes };
  const dialog = el('dialog', { className: 'tx-dialog hum-dialog', id: 'hum-review', 'aria-label': 'The melody in your hum' });
  const canvas = el('canvas', { width: 640, height: 180, className: 'hum-roll', 'aria-label': 'The notes that were found' });
  const info = el('p', { className: 'tx-hint', id: 'hum-info' });
  const list = el('div', { className: 'hum-notes', id: 'hum-notes' });
  const tempoInput = el('input', { type: 'number', value: state.tempo, min: 30, max: 300, step: 1, 'aria-label': 'Tempo', style: 'width:70px' });
  const gridSelect = el('select', { 'aria-label': 'Snap to' }, [['sixteenth', 0.25], ['eighth', 0.5], ['beat', 1], ['as sung (fine)', 0.0625]].map(([label, v]) => el('option', { value: v, textContent: label })));
  const keySelect = el('select', { 'aria-label': 'Key' }, [['as sung', ''], ['best fitting key', 'auto'], ...NAMES.flatMap((n) => [[`${n} major`, `${n} major`], [`${n} minor`, `${n} minor`]])].map(([label, v]) => el('option', { value: v, textContent: label })));
  const hearNotes = el('button', { type: 'button', id: 'hum-hear-notes', textContent: '▶ Hear the notes' });
  const hearAudio = el('button', { type: 'button', id: 'hum-hear-audio', textContent: '▶ Hear your hum' });
  const again = el('button', { type: 'button', textContent: '● Record again' });
  const accept = el('button', { type: 'button', id: 'hum-accept', className: 'primary', textContent: context.acceptLabel || 'Use this melody', disabled: true });
  const cancel = el('button', { type: 'button', textContent: 'Cancel' });
  const audio = registerPlaybackAudio(new Audio(URL.createObjectURL(new Blob([bytes], { type: 'audio/wav' }))));

  const draw = () => {
    const g = canvas.getContext('2d'), w = canvas.width, h = canvas.height;
    g.clearRect(0, 0, w, h);
    const notes = state.result?.notes || [];
    if (!notes.length) return;
    const lo = Math.min(...notes.map((n) => n.pitch)) - 2, hi = Math.max(...notes.map((n) => n.pitch)) + 2, total = Math.max(0.5, state.result.seconds);
    const color = getComputedStyle(document.documentElement).getPropertyValue('--acc').trim() || '#4da3ff';
    g.strokeStyle = 'rgba(255,255,255,.08)';
    for (let p = Math.ceil(lo); p <= hi; p++) { const y = h - ((p - lo) / (hi - lo)) * h; g.beginPath(); g.moveTo(0, y); g.lineTo(w, y); g.stroke(); if (p % 12 === 0) { g.fillStyle = 'rgba(255,255,255,.4)'; g.fillText(noteName(p), 3, y - 2); } }
    for (const n of notes) {
      const x = (n.start / total) * w, wd = Math.max(3, (n.duration / total) * w), y = h - ((n.pitch - lo) / (hi - lo)) * h - h / (hi - lo) / 2;
      g.globalAlpha = 0.45 + 0.55 * (n.strength ?? 1);
      g.fillStyle = color;
      g.fillRect(x, y - 4, wd, 8);
      g.globalAlpha = 1;
    }
  };
  const show = () => {
    const r = state.result;
    info.textContent = r ? `${r.quality} ${r.noteCount} note(s) in ${r.seconds} s${r.key ? ` · the key that fits best: ${r.key}` : ''}.` : 'Looking for the melody...';
    list.replaceChildren(...(r?.notes || []).map((n) => el('span', { className: 'hum-note', title: `${n.start.toFixed(2)} s, ${n.duration.toFixed(2)} s, ${n.cents > 0 ? '+' : ''}${n.cents} cents`, textContent: n.name })));
    accept.disabled = !r || r.noteCount === 0;
    draw();
  };
  let seq = 0;
  const run = async () => {
    const mine = ++seq;
    state.result = null; show();
    try {
      const r = await transcribe(bytes, { tempo: Number(tempoInput.value) || state.tempo, grid: Number(gridSelect.value), key: keySelect.value, bar: state.bar });
      if (mine === seq) { state.result = r; show(); }
    } catch (error) { if (mine === seq) { info.textContent = error.message || String(error); } }
  };
  for (const c of [tempoInput, gridSelect, keySelect]) c.onchange = run;
  hearNotes.onclick = () => { if (state.result?.notes.length) playNotes(state.result.notes, Number(tempoInput.value) || state.tempo); };
  hearAudio.onclick = () => { stopHearing(); audio.currentTime = 0; audio.play().catch(() => {}); };
  const shut = () => { stopHearing(); audio.pause(); URL.revokeObjectURL(audio.src); dialog.close(); dialog.remove(); };
  cancel.onclick = shut;
  again.onclick = () => { shut(); startHum(context).catch((error) => toast(error.message || String(error))); };
  accept.onclick = async () => {
    accept.disabled = true;
    try {
      await context.onAccept({ result: state.result, bytes, options: { tempo: Number(tempoInput.value) || state.tempo, grid: Number(gridSelect.value), key: keySelect.value, bar: state.bar } });
      shut();
    } catch (error) { toast(error.message || String(error)); accept.disabled = false; }
  };
  dialog.addEventListener('cancel', (e) => { e.preventDefault(); shut(); });
  dialog.append(
    el('div', { className: 'tx-head' }, [el('h2', { textContent: 'The melody in your hum' }), el('span', { className: 'sp' })]),
    el('div', { className: 'tx-body' }, [
      el('p', { className: 'tx-lead', textContent: 'AMPLE listened for the notes you hummed. It is an attempt: hear it next to your hum, and change how it is snapped when it is not what you meant.' }),
      canvas, info, list,
      el('div', { className: 'row hum-settings' }, [el('label', {}, ['tempo ', tempoInput]), el('label', {}, ['snap to ', gridSelect]), el('label', {}, ['key ', keySelect])]),
    ]),
    el('div', { className: 'tx-foot' }, [accept, hearNotes, hearAudio, again, cancel]));
  document.body.append(dialog);
  dialog.showModal();
  run();
}

/**
 * The hum attachment of a prompt or a message to Codex: a button that records and reviews a hum, and the list of the melodies that are attached. take() gives the ids and clears them.
 * Whatever the user approves is kept by the server and handed to Codex with the raw audio and the tool that turns it into notes.
 */
export function humAttachment({ onChange } = {}) {
  const hums = [];   // { id, label }
  const chips = el('span', { className: 'hum-chips', id: 'hum-chips' });
  const paint = () => {
    chips.replaceChildren(...hums.map((h, i) => el('span', { className: 'hum-chip', title: 'This melody goes to Codex with your message' }, [`♪ ${h.label}`, el('button', { type: 'button', className: 'mini', textContent: '×', 'aria-label': 'Remove this melody', onclick: () => { hums.splice(i, 1); paint(); onChange?.(); } })])));
  };
  const button = el('button', { type: 'button', id: 'hum-attach', textContent: '● Hum a melody...', title: 'Hum or sing what you mean: AMPLE finds the notes in it, and Codex gets the melody and your recording', onclick: () => {
    startHum({
      title: 'Hum a melody for Codex',
      acceptLabel: 'Approve and give it to Codex',
      onAccept: async ({ bytes, options, result }) => {
        const kept = await api.postBinary(`/api/ai/hum?tempo=${options.tempo}&grid=${options.grid}&key=${encodeURIComponent(options.key)}&bar=${options.bar}`, bytes);
        hums.push({ id: kept.id, label: `${result.noteCount} notes${result.key ? ', ' + result.key : ''} (${result.seconds} s)` });
        paint(); onChange?.();
      },
    }).catch((error) => toast(error.message || String(error)));
  } });
  return { button, chips, ids: () => hums.map((h) => h.id), take: () => { const ids = hums.map((h) => h.id); hums.length = 0; paint(); return ids; } };
}

let listening = null;
/** Plays or stops a kept audio file of the user (an offered sound or a hum). */
export function previewAttachment(id, button) {
  if (listening) { listening.audio.pause(); listening.button.textContent = '▶'; const same = listening.id === id; listening = null; if (same) return; }
  const audio = registerPlaybackAudio(new Audio(`/api/ai/attachment/${id}/audio`));
  listening = { id, audio, button };
  button.textContent = '■';
  audio.onended = () => { button.textContent = '▶'; if (listening?.id === id) listening = null; };
  audio.play().catch(() => { button.textContent = '▶'; });
}

/**
 * Audio the user offers to Codex as material: dragged onto the window, picked from a file, or recorded with the microphone. It is only kept for this task: Codex may use it from a path like
 * samples/offered/name.wav, and the user decides at Accept which of it comes along into the project. take() gives the ids and clears the list.
 */
export function offerAttachment({ onChange, dropTarget } = {}) {
  const items = [];   // { id, name, seconds }
  const chips = el('span', { className: 'hum-chips offer-chips', id: 'offer-chips' });
  const paint = () => {
    chips.replaceChildren(...items.map((it, i) => {
      const play = el('button', { type: 'button', className: 'mini', textContent: '▶', title: 'Listen to it', 'aria-label': `Listen to ${it.name}`, onclick: () => previewAttachment(it.id, play) });
      return el('span', { className: 'hum-chip', title: 'Offered to Codex: it comes into the project only if you choose so when you accept' }, [play, `${it.name} (${it.seconds.toFixed(1)} s)`, el('button', { type: 'button', className: 'mini', textContent: '×', 'aria-label': `Do not offer ${it.name}`, onclick: () => { items.splice(i, 1); paint(); onChange?.(); } })]);
    }));
  };
  const send = async (bytes, name, extension = '') => {
    const r = await api.postBinary(`/api/ai/attachment?name=${encodeURIComponent(name)}${extension ? '&extension=' + extension : ''}`, bytes);
    items.push({ id: r.id, name: r.name, seconds: r.seconds });
    paint(); onChange?.();
  };
  const upload = async (file) => {
    const extension = (/\.[a-z0-9]+$/i.exec(file.name)?.[0] || '.wav').toLowerCase();
    if (!['.wav', '.mp3', '.ogg', '.flac', '.m4a'].includes(extension)) { toast(`${file.name} is not a sound (wav, mp3, m4a, ogg or flac).`); return; }
    try { await send(new Uint8Array(await file.arrayBuffer()), file.name, extension === '.wav' ? '' : extension); } catch (error) { toast(error.message || String(error)); }
  };
  const pick = el('input', { type: 'file', accept: 'audio/*,.wav,.mp3,.ogg,.flac,.m4a', multiple: true, hidden: true, id: 'offer-file' });
  pick.onchange = async () => { for (const f of pick.files) await upload(f); pick.value = ''; };
  const add = el('button', { type: 'button', id: 'offer-add', textContent: '＋ Add audio...', title: 'Offer a sound to Codex: pick a file, or drag one onto this window', onclick: () => pick.click() });
  let recordings = 0;
  const record = el('button', { type: 'button', id: 'offer-record', textContent: '● Record a sound...', title: 'Record a sound with the microphone and offer it to Codex', onclick: () => {
    openRecorder({ title: 'Record a sound for Codex', hum: false, useLabel: 'Offer it to Codex', onUse: async (bytes) => { await send(bytes, `recording-${++recordings}.wav`); } }).catch((error) => toast(error.message || String(error)));
  } });
  if (dropTarget) {
    const over = (e) => { if ([...(e.dataTransfer?.types || [])].includes('Files')) { e.preventDefault(); dropTarget.classList.add('offer-drop'); } };
    dropTarget.addEventListener('dragover', over);
    dropTarget.addEventListener('dragenter', over);
    dropTarget.addEventListener('dragleave', (e) => { if (e.target === dropTarget) dropTarget.classList.remove('offer-drop'); });
    dropTarget.addEventListener('drop', async (e) => {
      dropTarget.classList.remove('offer-drop');
      if (!e.dataTransfer?.files?.length) return;
      e.preventDefault();
      for (const f of e.dataTransfer.files) await upload(f);
    });
  }

  return { add, record, pick, chips, ids: () => items.map((i) => i.id), take: () => { const ids = items.map((i) => i.id); items.length = 0; paint(); return ids; } };
}
