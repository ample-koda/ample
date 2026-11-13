// Performance mixer (issue 276): a temporary mixer for rehearsal and live control. Faders, pans, mutes, solos and live effect moves (also from MIDI CC and snapshot recall) are
// kept in a session layer and heard through the live chain; the song is NOT edited until "Apply to project" (one undo step). "Reset" brings the saved mix back.
// HOOK for the Performance workspace (issue 266): import { performanceMixerPanel } from './performance-mixer.js' and append performanceMixerPanel() where the mixer should show.
import { el } from './ui.js';
import { edit, flat } from './store.js';
import { chainPreview } from './live-chain.js';
import { createSession, muteGains, soloGains, quote } from '../lib/performance-mixer-model.mjs';

export const session = createSession();
const parts = (value, fallback) => String(value || fallback).replace(/^"|"$/g, '').trim().split(/\s+/);

/** The one dispatcher: mouse, touch and MIDI CC all call this. Heard at once (live chain), kept in the session, the song is untouched. */
export function performSet(block, name, value) { session.set(block, name, value); chainPreview(block, name, value); }

/** Reset: the saved mix is back, in the sound too (the live chain gets the saved values again). */
export function performReset(nodes = flat()) {
  const touched = session.entries();
  session.reset();
  for (const { block, name } of touched) { const saved = nodes.find((n) => n.id === block)?.params?.[name]; if (saved !== undefined) chainPreview(block, name, saved); }
}

/** Apply to project: every override as one edit batch (one undo step), then the session starts clean. */
export function performApply() { const edits = session.applyEdits(); if (!edits.length) return 0; edit(edits); session.applied(); return edits.length; }

/** A snapshot recall in Performance mode goes to the session instead of the song: use this as the panel's commitMany for a block. */
export const performCommitMany = (block) => (writes) => { for (const { name, value } of writes) performSet(block, name, value); };

/** The inputs of a mixer or bus as session controls: gain slider, pan, mute, solo. */
function strip(node) {
  const gains = () => parts(session.read(node.id, 'gains', node.params.gains), '0 0 0 0');
  const pans = () => parts(session.read(node.id, 'pan', node.params.pan), 'c c c c');
  const row = el('div', { className: 'perfmix-strip', 'data-block': node.id }, [el('strong', { textContent: node.title || node.id })]);
  for (let i = 0; i < 4; i++) {
    const readout = el('output', { textContent: `${Number(gains()[i]) || 0} dB` });
    const fader = el('input', { type: 'range', min: '-60', max: '12', step: '1', value: String(Number(gains()[i]) || 0), 'aria-label': `${node.id} input ${i + 1} gain` });
    fader.oninput = () => { const g = gains(); g[i] = fader.value; readout.textContent = `${fader.value} dB`; performSet(node.id, 'gains', quote(g)); };
    const pan = el('select', { 'aria-label': `${node.id} input ${i + 1} pan` }, [['l', 'L'], ['c', 'C'], ['r', 'R']].map(([v, t]) => el('option', { value: v, textContent: t, selected: (pans()[i] || 'c').toLowerCase() === v })));
    pan.onchange = () => { const p = pans(); p[i] = pan.value; performSet(node.id, 'pan', quote(p)); };
    const mute = el('button', { type: 'button', textContent: 'M', 'aria-label': `${node.id} input ${i + 1} mute`, onclick: () => { performSet(node.id, 'gains', quote(muteGains(gains(), i))); fader.value = '-60'; readout.textContent = '-60 dB'; } });
    const solo = el('button', { type: 'button', textContent: 'S', 'aria-label': `${node.id} input ${i + 1} solo`, onclick: () => performSet(node.id, 'gains', quote(soloGains(gains(), i))) });
    row.append(el('div', { className: 'perfmix-chan' }, [pan, fader, readout, mute, solo]));
  }
  return row;
}

/** The panel for the Performance workspace: strips of every mixer and bus, plus Reset and Apply to project. */
export function performanceMixerPanel() {
  const count = el('span', { className: 'note', textContent: '' });
  const paintCount = () => { count.textContent = session.size ? `${session.size} change${session.size === 1 ? '' : 's'} in this session, not in the song` : 'Song mix (nothing changed)'; };
  session.subscribe(paintCount); paintCount();
  const reset = el('button', { type: 'button', textContent: 'Reset', title: 'Back to the saved mix', onclick: () => performReset() });
  const apply = el('button', { type: 'button', textContent: 'Apply to project', title: 'Write these settings into the song (one undo step)', onclick: () => performApply() });
  const strips = flat().filter((n) => n.tpl === 'mixer' || n.tpl === 'bus').map(strip);
  return el('section', { className: 'perfmix', 'aria-label': 'Performance mixer' }, [el('div', { className: 'perfmix-bar' }, [el('strong', { textContent: 'Performance mixer' }), count, reset, apply]), ...strips]);
}
