import { playbackOutput } from './playback-volume.js';
// Recording from the microphone (plan X07): record in the browser, listen, trim, set a latency offset, and only then upload one take
// into the project as a WAV with an import-audio block. Nothing leaves the browser before "Use in project". @ample:recorder

import { ask, el, toast } from './ui.js';
import { api } from './api.js';
import { S, edit, flat, notify, readOnly, select } from './store.js';
import { play, stop as stopPlayback } from './player.js';
import { applyOffset, cutBefore, encodeWav, findOnset, joinChunks, levelOf, normalize, peaksOf, takeFileName, trim } from '../lib/recorder-model.mjs';
import { barBeats } from '../lib/meter.mjs';

// every chunk carries the frame of the audio clock it starts at, so a recording can be cut at an exact frame (the downbeat of the song)
const WORKLET = 'class Tap extends AudioWorkletProcessor{process(i){const c=i[0]&&i[0][0];if(c)this.port.postMessage({frame:currentFrame,data:c.slice(0)});return true}}registerProcessor("ample-tap",Tap)';

let session = null;   // the open recorder: { stream, context, chunks, takes, ... }

/** options (all optional): { title, tempo, useLabel, onUse(bytes) }: with onUse the recorder is a way to get a recording to something else (a hum) and nothing is added to the song. */
export async function openRecorder(options = {}) {
  if (readOnly() && !options.onUse && typeof options.folder !== 'string') { toast('This song is read-only.'); return; }
  if (!navigator.mediaDevices?.getUserMedia) { toast('This browser cannot record: the microphone needs a secure page (localhost or HTTPS).'); return; }
  if (session) return;
  const tempo = options.tempo || Number(S.doc?.project?.tempo) || 120;
  const takes = [];
  let current = null, recording = false, chunks = [], context = null, stream = null, source = null, node = null, rate = 48000, playing = null, firstFrame = -1;
  const dialog = el('dialog', { className: 'recorder', 'aria-label': 'Record from the microphone' });
  const device = el('select', { 'aria-label': 'Input device' });
  const meter = el('div', { className: 'rec-meter' }, [el('i')]);
  const raw = el('input', { type: 'checkbox', id: 'rec-process' });
  const countIn = el('input', { type: 'checkbox', id: 'rec-count', checked: true });
  const backing = el('input', { type: 'checkbox', id: 'rec-backing' });
  const recordButton = el('button', { className: 'primary', textContent: '● Record', disabled: true });
  const status = el('span', { className: 'note', textContent: 'The recording stays in this browser until you press Use in project.' });
  const list = el('div', { className: 'rec-takes' });
  const canvas = el('canvas', { width: 600, height: 70, className: 'rec-wave' });
  const startRange = el('input', { type: 'range', min: 0, max: 1000, value: 0, 'aria-label': 'Trim start' });
  const endRange = el('input', { type: 'range', min: 0, max: 1000, value: 1000, 'aria-label': 'Trim end' });
  const offset = el('input', { type: 'number', value: 0, step: 5, style: 'width:70px', 'aria-label': 'Latency in milliseconds' });
  const normalise = el('input', { type: 'checkbox', id: 'rec-norm' });
  const listen = el('button', { textContent: '▶ Listen', disabled: true });
  const use = el('button', { className: 'primary', textContent: options.useLabel || 'Use in project', disabled: true });
  const melody = options.onUse ? null : el('button', { textContent: 'Find melody', title: 'Find the melody in this recording (a hum, a sung line) and add it as a midi-clip with notes', disabled: true });
  const calibrate = el('button', { textContent: 'Calibrate latency', title: 'Plays a click through the speakers and listens for it with the microphone; put the microphone near the speaker' });
  const close = el('button', { textContent: 'Close' });

  const processed = (take) => {
    let out = trim(take.samples, startRange.value / 1000, endRange.value / 1000);
    out = applyOffset(out, Number(offset.value) || 0, take.rate);
    return normalise.checked ? normalize(out) : out;
  };
  const draw = () => {
    const g = canvas.getContext('2d');
    g.clearRect(0, 0, canvas.width, canvas.height);
    if (!current) return;
    const p = peaksOf(current.samples, canvas.width);
    g.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--acc').trim() || '#4aa3ff';
    for (let x = 0; x < canvas.width; x++) g.fillRect(x, 35 - p[x * 2 + 1] * 35, 1, Math.max(1, (p[x * 2 + 1] - p[x * 2]) * 35));
    g.fillStyle = 'rgba(0,0,0,.45)';
    g.fillRect(0, 0, (startRange.value / 1000) * canvas.width, canvas.height);
    g.fillRect((endRange.value / 1000) * canvas.width, 0, canvas.width, canvas.height);
  };
  const renderList = () => {
    list.replaceChildren(...takes.map((t, i) => {
      const row = el('button', { className: `rec-take${t === current ? ' on' : ''}`, textContent: `Take ${i + 1} · ${(t.samples.length / t.rate).toFixed(1)} s` });
      row.onclick = () => { current = t; startRange.value = 0; endRange.value = 1000; renderList(); draw(); listen.disabled = use.disabled = false; if (melody) melody.disabled = false; };
      const drop = el('button', { className: 'mini', textContent: '×', 'aria-label': 'Discard this take', title: 'Discard this take' });
      drop.onclick = async () => { if (!(await ask('Discard take', `Discard take ${i + 1}? It is not saved anywhere else.`, [{ label: 'Discard', value: true, primary: true }, { label: 'Keep', value: false }]))) return; takes.splice(i, 1); if (current === t) { current = takes.at(-1) || null; listen.disabled = use.disabled = !current; if (melody) melody.disabled = !current; } renderList(); draw(); };
      return el('div', { className: 'rec-take-row' }, [row, drop]);
    }));
  };
  // a click at a time of the audio clock (the count-in and the calibration are scheduled on it, not with timers)
  const click = (at = context.currentTime) => {
    const o = context.createOscillator(), gain = context.createGain();
    o.frequency.value = 1000; gain.gain.value = 0.2; o.connect(gain).connect(context.destination);
    o.start(at); o.stop(at + 0.04);
  };
  const until = async (time) => { while (context.currentTime < time) await new Promise((resolve) => setTimeout(resolve, Math.max(1, (time - context.currentTime) * 1000 - 4))); };
  const open = async (deviceId) => {
    recordButton.disabled = true;
    stream?.getTracks().forEach((t) => t.stop());
    context?.close();
    stream = await navigator.mediaDevices.getUserMedia({ audio: { deviceId: deviceId ? { exact: deviceId } : undefined, echoCancellation: raw.checked, noiseSuppression: raw.checked, autoGainControl: raw.checked, channelCount: 1 } });
    context = new AudioContext();
    rate = context.sampleRate;
    await context.audioWorklet.addModule(URL.createObjectURL(new Blob([WORKLET], { type: 'text/javascript' })));
    source = context.createMediaStreamSource(stream);
    node = new AudioWorkletNode(context, 'ample-tap');
    node.port.onmessage = (event) => {
      const { frame, data } = event.data;
      meter.firstChild.style.width = `${Math.round(levelOf(data) * 100)}%`;
      if (recording) { if (!chunks.length) firstFrame = frame; chunks.push(data); }
    };
    source.connect(node);
    const devices = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === 'audioinput');
    device.replaceChildren(...devices.map((d) => el('option', { value: d.deviceId, textContent: d.label || 'Microphone', selected: stream.getAudioTracks()[0]?.getSettings().deviceId === d.deviceId })));
    recordButton.disabled = false;
  };
  let startFrame = 0;   // the frame of the downbeat (bar 1), set when a recording starts
  const finish = () => {
    recording = false;
    recordButton.textContent = '● Record';
    stopPlayback();
    if (chunks.length) {
      const samples = cutBefore(joinChunks(chunks), firstFrame, startFrame);   // the take starts at the downbeat of the song, the count-in is cut away
      if (samples.length) {
        const take = { samples, rate };
        takes.push(take);
        current = take;
        listen.disabled = use.disabled = false; if (melody) melody.disabled = false;
        renderList(); draw();
      }
    }
    chunks = [];
  };
  recordButton.onclick = async () => {
    if (recording) { finish(); return; }
    if (!stream) return;
    await context.resume();
    // everything is collected from now on and cut at the downbeat when the take is made: the clicks are scheduled on the audio clock, and the first bar of the song starts at the frame after the fourth click
    chunks = []; firstFrame = -1; startFrame = Number.MAX_SAFE_INTEGER; recording = true;   // stopping during the count-in leaves an empty take, which is dropped
    recordButton.textContent = '■ Stop';
    const beat = 60 / tempo;
    let downbeat;
    if (countIn.checked) {
      status.textContent = 'Count-in...';
      const first = context.currentTime + 0.2;
      for (let i = 0; i < 4; i++) click(first + i * beat);
      downbeat = first + 4 * beat;
      await until(downbeat);
    } else downbeat = context.currentTime;
    if (!recording) return;
    startFrame = Math.round(downbeat * rate);
    if (backing.checked) { const take = S.build?.takes?.master; if (take) { await play(take).catch(() => {}); startFrame = Math.round(context.currentTime * rate); } }   // recording is cut at the moment the song is really playing
    status.textContent = 'Recording...';
  };
  listen.onclick = async () => {
    if (!current) return;
    if (playing) { playing.stop(); playing = null; return; }
    const data = processed(current);
    const buffer = context.createBuffer(1, data.length, current.rate);
    buffer.copyToChannel(data, 0);
    playing = context.createBufferSource();
    playing.buffer = buffer;
    playing.connect(playbackOutput(context));
    playing.onended = () => { playing = null; listen.textContent = '▶ Listen'; };
    playing.start();
    listen.textContent = '■ Stop';
  };
  calibrate.onclick = async () => {
    if (recording || !stream) return;
    await context.resume();
    chunks = []; firstFrame = -1;
    recording = true;
    status.textContent = 'Listening for the click...';
    const at = context.currentTime + 0.2;
    click(at);
    await new Promise((resolve) => setTimeout(resolve, 900));
    recording = false;
    // the latency is the distance between the frame the click was scheduled at and the frame the microphone heard it
    const heard = cutBefore(joinChunks(chunks), firstFrame, Math.round(at * rate));
    const onset = findOnset(heard);
    chunks = [];
    if (onset < 0) { status.textContent = 'The click was not heard: move the microphone close to the speaker and try again.'; return; }
    offset.value = Math.round((onset / rate) * 1000);
    status.textContent = `Latency set to ${offset.value} ms (it is subtracted from every take).`;
    draw();
  };
  for (const input of [startRange, endRange, offset, normalise]) input.oninput = draw;
  device.onchange = () => open(device.value).catch((e) => toast(e.message));
  raw.onchange = () => open(device.value).catch((e) => toast(e.message));
  if (melody) {
    melody.onclick = async () => {
      if (!current) return;
      melody.disabled = true;
      try {
        const { transcribe, songTempo } = await import('./hum.js');
        const r = await transcribe(encodeWav(processed(current), current.rate), { tempo: songTempo(), grid: 0.25, key: '', bar: 1 });
        if (!r.noteCount) { toast(r.quality); return; }
        let name = 'hum', n = 1;
        const used = new Set(flat().nodes.map((b) => b.id));
        while (used.has(n === 1 ? name : `${name}${n}`)) n++;
        name = n === 1 ? name : `${name}${n}`;
        const bars = Math.max(1, Math.ceil((r.seconds * songTempo()) / (barBeats() * 60)));
        if (await edit([{ type: 'addBlock', blockType: 'midi-clip', name }, { type: 'setParameter', block: name, name: 'notes', value: r.notesText }, { type: 'setParts', block: name, parts: [{ at: 1, length: bars, name: '' }] }])) { select(name); notify(); toast(`Added ${name} with ${r.noteCount} notes${r.key ? ' (' + r.key + ')' : ''}: an attempt, open the piano roll to correct it.`); }
      } catch (e) { toast(e.message || String(e)); } finally { melody.disabled = !current; }
    };
  }
  use.onclick = async () => {
    if (!current) return;
    use.disabled = true;
    if (options.onUse) {
      try { await options.onUse(encodeWav(processed(current), current.rate)); shutdown(); } catch (e) { toast(e.message || String(e)); use.disabled = false; }
      return;
    }
    try {
      const number = (S.nextRecording = (S.nextRecording || 0) + 1);
      const file = new File([encodeWav(processed(current), current.rate)], takeFileName(number), { type: 'audio/wav' });
      // started from a folder of the project: the take is saved in that folder and nothing is added to the song
      if (typeof options.folder === 'string') {
        const saved = await api.uploadProject(file, options.folder, 'rename', () => {});
        toast(`Saved ${saved.path || file.name}`);
        options.onSaved?.(saved.path || file.name);
        return;
      }
      const result = await api.uploadProject(file, 'recordings', 'rename', () => {});
      const path = result.path || `recordings/${file.name}`;
      let name = 'recording', n = 1;
      const used = new Set(flat().nodes.map((b) => b.id));
      while (used.has(n === 1 ? name : `${name}${n}`)) n++;
      name = n === 1 ? name : `${name}${n}`;
      if (await edit([{ type: 'addBlock', blockType: 'import-audio', name }, { type: 'setParameter', block: name, name: 'file', value: path }])) { select(name); notify(); toast(`Added ${name} from ${path}`); }
    } catch (e) { toast(e.message); } finally { use.disabled = !current; }
  };
  const shutdown = () => { stream?.getTracks().forEach((t) => t.stop()); context?.close(); session = null; dialog.close(); dialog.remove(); };
  const requestClose = async () => {
    if (takes.length && !(await ask('Close recorder', `${takes.length} take${takes.length === 1 ? '' : 's'} not used yet will be lost. Close anyway?`, [{ label: 'Close', value: true, primary: true }, { label: 'Keep recording', value: false }]))) return;
    shutdown();
  };
  close.onclick = requestClose;
  dialog.addEventListener('cancel', (event) => { event.preventDefault(); requestClose(); });
  dialog.append(
    el('h2', { textContent: options.title || 'Record from the microphone' }),
    el('div', { className: 'row' }, [device, meter]),
    el('div', { className: 'row' }, [el('label', {}, [raw, ' browser noise processing']), el('label', {}, [countIn, ' count-in']), el('label', {}, [backing, ' play the song while recording'])]),
    el('div', { className: 'row' }, [recordButton, status]),
    list, canvas,
    el('div', { className: 'row' }, [el('label', {}, ['start ', startRange]), el('label', {}, ['end ', endRange])]),
    el('div', { className: 'row' }, [el('label', {}, ['latency ms ', offset]), calibrate, el('label', {}, [normalise, ' normalise']), listen, ...(melody ? [melody] : []), use, close]),
  );
  document.body.append(dialog);
  dialog.showModal();
  session = {};
  try { await open(); } catch (e) { toast(e.name === 'NotAllowedError' ? 'The microphone is blocked. Allow it in the browser address bar and try again.' : e.message); shutdown(); }
}
