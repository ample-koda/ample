// Performance mixer (issue 276): a temporary mixer for rehearsal and live control. Faders, pans, mutes, solos and live effect moves (also from MIDI CC and snapshot recall) are
// kept in a session layer and heard through the live chain; the song is NOT edited until "Apply to project" (one undo step). "Reset" brings the saved mix back.
// HOOK for the Performance workspace (issue 266): import { performanceMixerPanel } from './performance-mixer.js' and append performanceMixerPanel() where the mixer should show.
// The rules (clamping, mute and solo, Apply as one edit that keeps the overrides when it fails) live in ../lib/performance-mixer-model.mjs (createPerformMixer). @ample:performance-mixer
import { el } from './ui.js';
import { edit, flat, subscribe } from './store.js';
import { chainPreview, chainRevert } from './live-chain.js';
import { createPerformMixer, createSession, parts, GAIN_MIN, GAIN_MAX } from '../lib/performance-mixer-model.mjs';

export const session = createSession();
const mixer = createPerformMixer({ session, nodes: () => flat().nodes, preview: chainPreview, revert: chainRevert, edit });
let watching = false;
const watchSong = () => { if (!watching) { watching = true; subscribe(() => { if (session.size) mixer.resync(); }); } };   // the song changed under the session: the sound follows the session again

/** The one dispatcher: mouse, touch and MIDI CC all call this. Heard at once (live chain), kept in the session, the song is untouched. range = { min, max } of the parameter when it is known. */
export function performSet(block, name, value, range) { watchSong(); return mixer.performSet(block, name, value, range); }

/** Reset: the saved mix is back, in the sound too. */
export const performReset = () => mixer.performReset();

/** Apply to project: every override as ONE edit batch (one undo step). The session is cleared only when the edit worked. Resolves to { applied, skipped, ok }. */
export const performApply = () => mixer.performApply();

/** A snapshot recall in Performance mode goes to the session instead of the song: use this as the panel's commitMany for a block. */
export const performCommitMany = (block) => (writes) => { for (const { name, value } of writes) performSet(block, name, value); };

/** The inputs of a mixer or bus as session controls: gain slider, pan, mute, solo. */
function strip(node) {
  const saved = (name) => flat().nodes.find((n) => n.id === node.id)?.params?.[name] ?? node.params?.[name];
  const gains = () => parts(session.read(node.id, 'gains', saved('gains')), '0 0 0 0');
  const pans = () => parts(session.read(node.id, 'pan', saved('pan')), 'c c c c');
  const row = el('div', { className: 'perfmix-strip', 'data-block': node.id }, [el('strong', { textContent: node.title || node.id })]);
  const paints = [];
  for (let i = 0; i < 4; i++) {
    const readout = el('output', { textContent: '' });
    const fader = el('input', { type: 'range', min: String(GAIN_MIN), max: String(GAIN_MAX), step: '1', value: '0', 'aria-label': `${node.id} input ${i + 1} gain` });
    fader.oninput = () => { watchSong(); mixer.performSet(node.id, 'gains', `"${gains().map((g, k) => (k === i ? fader.value : g)).join(' ')}"`); };
    const pan = el('select', { 'aria-label': `${node.id} input ${i + 1} pan` }, [['l', 'L'], ['c', 'C'], ['r', 'R']].map(([v, t]) => el('option', { value: v, textContent: t })));
    pan.onchange = () => { watchSong(); mixer.performSet(node.id, 'pan', `"${pans().map((p, k) => (k === i ? pan.value : p)).join(' ')}"`); };
    const mute = el('button', { type: 'button', textContent: 'M', 'aria-label': `${node.id} input ${i + 1} mute`, onclick: () => { watchSong(); mixer.performMute(node.id, i); } });
    const solo = el('button', { type: 'button', textContent: 'S', 'aria-label': `${node.id} input ${i + 1} solo`, onclick: () => { watchSong(); mixer.performSolo(node.id, i); } });
    paints.push(() => {
      const g = Number(gains()[i]) || 0;
      fader.value = String(g); readout.textContent = `${g} dB`;   // the fader keeps its own position while the input is muted
      pan.value = (pans()[i] || 'c').toLowerCase();
      mute.setAttribute('aria-pressed', String(session.isMuted(node.id, i))); solo.setAttribute('aria-pressed', String(session.isSoloed(node.id, i)));
    });
    row.append(el('div', { className: 'perfmix-chan' }, [pan, fader, readout, mute, solo]));
  }
  row.paint = () => paints.forEach((p) => p());
  row.paint();
  return row;
}

/** The panel for the Performance workspace: strips of every mixer and bus, plus Reset and Apply to project. */
export function performanceMixerPanel() {
  watchSong();
  const count = el('span', { className: 'note', textContent: '' });
  const strips = flat().nodes.filter((n) => n.tpl === 'mixer' || n.tpl === 'bus').map(strip);
  let seen = false, off = () => {};
  const paint = () => {
    if (count.isConnected) seen = true; else if (seen) { off(); return; }   // the panel was removed: stop listening
    count.textContent = session.size ? `${session.size} change${session.size === 1 ? '' : 's'} in this session, not in the song` : 'Song mix (nothing changed)';
    strips.forEach((s) => s.paint());
  };
  off = session.subscribe(paint); paint();
  const reset = el('button', { type: 'button', textContent: 'Reset', title: 'Back to the saved mix', onclick: () => performReset() });
  const apply = el('button', { type: 'button', textContent: 'Apply to project', title: 'Write these settings into the song (one undo step)', onclick: () => { performApply().then((r) => { if (!r.ok && !r.busy) count.textContent = 'Apply failed: the changes are kept in this session'; }); } });
  return el('section', { className: 'perfmix', 'aria-label': 'Performance mixer' }, [el('div', { className: 'perfmix-bar' }, [el('strong', { textContent: 'Performance mixer' }), count, reset, apply]), ...strips]);
}
