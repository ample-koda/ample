import { api, getToken } from './api.js';
import { ask, el, toast } from './ui.js';
import { compareMidiNotes } from '../lib/midi-compare.js';
import { pause, play, playerState, seekFraction, onPlayer } from './player.js';

async function notesOf(takeId) {
  const take = await api.get(`/api/takes/${encodeURIComponent(takeId)}`);
  const ref = take.take.data.find(x => x.role === 'notes');
  if (!ref) throw new Error(`Take ${takeId} has no note data.`);
  const response = await fetch(`/api/data/${encodeURIComponent(ref.dataId)}`, { headers: { 'X-Ample-Token': getToken() } });
  if (!response.ok) throw new Error(`Could not read note data (${response.status}).`);
  return JSON.parse(await response.text()).notes;
}

/** Opens the MIDI A/B view. Audio take IDs and note take IDs may differ. */
export async function openMidiCompare(file, defaultOriginalAudio, defaultOriginalNotes, availableTakes) {
  const takeList = el('datalist', { id: 'midi-compare-takes' });
  for (const [block, takeId] of Object.entries(availableTakes)) takeList.append(el('option', { value: takeId, label: block }));
  const field = (label, value, placeholder, type = 'text') => {
    const input = el('input', { type, value: value || '', placeholder, min: type === 'number' ? '0' : '', step: type === 'number' ? '1' : '', style: 'width:100%;box-sizing:border-box' });
    if (type === 'text') input.setAttribute('list', takeList.id);
    return { input, row: el('label', { textContent: label, style: 'display:grid;grid-template-columns:145px 1fr;align-items:center;gap:8px' }, [input]) };
  };
  const aAudio = field('Original audio take', defaultOriginalAudio, 'Build the MIDI block first');
  const bAudio = field('Converted audio take', '', 'Build the converted song first');
  const aNotes = field('Original note take', defaultOriginalNotes, 'MIDI block notes');
  const bNotes = field('Converted note take', '', 'Converted midi-clip notes');
  const tolerance = field('Onset tolerance (ticks)', '0', '', 'number');
  const rows = el('div', { style: 'display:grid;gap:9px;width:min(560px,80vw)' }, [takeList, aAudio.row, bAudio.row, aNotes.row, bNotes.row, tolerance.row]);
  if (await ask('Compare MIDI playback', 'Select takes from this project. Build the source MIDI block and converted song first. A/B playback and listening review are manual.', [{ label: 'Cancel', value: 'cancel' }, { label: 'Compare', value: 'compare', primary: true }], [rows]) !== 'compare') return;
  const originalAudio = aAudio.input.value.trim(), convertedAudio = bAudio.input.value.trim();
  const originalNotes = aNotes.input.value.trim(), convertedNotes = bNotes.input.value.trim();
  const tickTolerance = tolerance.input.valueAsNumber;
  if (!originalAudio || !convertedAudio || !originalNotes || !convertedNotes || !Number.isSafeInteger(tickTolerance) || tickTolerance < 0) { toast('Choose four valid take IDs and a non-negative whole-tick tolerance.'); return; }
  try {
    const [a, b, info] = await Promise.all([notesOf(originalNotes), notesOf(convertedNotes), api.get(`/api/midi/info?file=${encodeURIComponent(file)}`)]);
    const report = compareMidiNotes(a, b, info.ppq, tickTolerance);
    const dialog = el('div', { className: 'modal', style: 'position:fixed;inset:0;z-index:10000;background:var(--backdrop);display:grid;place-items:center;padding:20px' });
    const card = el('div', { style: 'background:var(--panel);color:var(--text);padding:20px;border-radius:12px;min-width:min(520px,90vw)' });
    const state = el('p', { textContent: 'Paused' });
    const seek = el('input', { type: 'range', min: 0, max: 1000, value: 0, style: 'width:100%' });
    let active = 'A';
    let hasStarted = false;
    let pendingSeek = null;
    let unlisten = () => {};
    const sync = () => { const p = playerState(); state.textContent = `${active} · ${p.playing ? 'Playing' : 'Paused'} · ${p.time.toFixed(1)} / ${p.duration.toFixed(1)} s`; seek.value = String(p.duration ? Math.round(p.time / p.duration * 1000) : 0); };
    const switchTo = async side => { const p = playerState(); pendingSeek = hasStarted && p.duration ? p.time / p.duration : 0; hasStarted = true; active = side; await play(side === 'A' ? originalAudio : convertedAudio, 0); if (playerState().duration) { seekFraction(pendingSeek); pendingSeek = null; } sync(); };
    card.append(el('h2', { textContent: 'MIDI A/B comparison' }), el('p', { textContent: `Notes found ${report.found} · missing ${report.missing} · extra ${report.extra}` }), el('p', { textContent: `Timing max/mean ${report.timing.maxTicks}/${report.timing.meanTicks} ticks · velocity max/mean ${report.velocity.max}/${report.velocity.mean}` }), el('p', { textContent: 'A and B switch the supplied audio takes at the current playback position. Listening review is manual.' }),
      el('div', { style: 'display:flex;gap:8px' }, [el('button', { textContent: 'Play A', onclick: () => switchTo('A').catch(e => toast(e.message)) }), el('button', { textContent: 'Play B', onclick: () => switchTo('B').catch(e => toast(e.message)) }), el('button', { textContent: 'Pause', onclick: pause })]), seek, state,
      el('button', { textContent: 'Close', onclick: () => { unlisten(); dialog.remove(); } }));
    seek.oninput = () => seekFraction(Number(seek.value) / 1000);
    unlisten = onPlayer(() => { if (pendingSeek !== null && playerState().duration) { seekFraction(pendingSeek); pendingSeek = null; } sync(); });
    dialog.append(card);
    document.body.append(dialog);
    sync();
  } catch (error) { toast(error.message || String(error)); }
}
