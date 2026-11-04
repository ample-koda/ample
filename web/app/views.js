// Which view fills the canvas area: the node editor (blocks), the source editor, the library manager, the branch manager,
// the lyrics editor or the plugin settings. Each view registers a renderer; leaving the source editor needs a valid song.
// @ample:layout @ample:source-editor

import { $, ask } from './ui.js';
import { errors, S, unsupportedFeatures } from './store.js';
import { toast } from './ui.js';

export const V = { mode: 'blocks', renderers: {}, guard: null };

const SHOWN = { src: '#se', lib: '#lb', bm: '#bm', lyr: '#ly', plug: '#pg', pat: '#pt', seq: '#sq' };

export function registerView(mode, render) { V.renderers[mode] = render; }

/** Switches the view. Returns false when the switch was refused (the source editor has changes that do not compile). */
export function setView(mode, force = false) {
  if (V.mode === 'src' && mode !== 'src' && !force && V.guard && !V.guard()) return false;
  V.mode = mode;
  document.dispatchEvent(new CustomEvent('ample:view', { detail: mode }));
  const wrap = $('#canvaswrap');
  wrap.classList.toggle('bmode', mode !== 'blocks');
  wrap.classList.toggle('lymode', mode === 'lyr' || mode === 'plug' || mode === 'pat' || mode === 'seq');
  Object.entries(SHOWN).forEach(([m, sel]) => { $(sel).style.display = mode === m ? 'flex' : 'none'; });
  const buttons = { blocks: '#vs-blocks', src: '#vs-source', lib: '#vs-library', bm: '#vs-branches' };
  Object.entries(buttons).forEach(([m, sel]) => $(sel).classList.toggle('on', mode === m));
  if (V.renderers[mode]) V.renderers[mode]();
  updateBanner();
  return true;
}

/** The banner separates blocking document errors from editable MIDI modules with retained source events. */
export function updateBanner() {
  const n = errors().length;
  const features = unsupportedFeatures();
  // the module source stays one click away for modules that carry retained MIDI events; the warning (info icon) only shows what the node view cannot display
  const retained = V.mode === 'blocks' && (features.length > 0 || (S.moduleStack.length > 0 && /^\s*# source-event /m.test(S.moduleDoc?.text || '')));
  $('#robanner').style.display = V.mode === 'blocks' && (n || retained) ? 'flex' : 'none';
  $('#ro-midi-info').style.display = features.length ? '' : 'none';
  $('#ro-n').textContent = String(n);
  $('#ro-file').textContent = S.modulePath || S.path || 'the song';
  $('#ro-errors').style.display = n ? 'inline' : 'none';
  $('#midi-gate').style.display = retained ? 'inline-flex' : 'none';
  $('#ro-back').style.display = n ? '' : 'none';
}

export function initViews() {
  $('#vs-blocks').onclick = () => setView('blocks');
  $('#vs-source').onclick = () => setView('src');
  $('#vs-library').onclick = () => setView('lib');
  $('#vs-branches').onclick = () => setView('bm');
  $('#ro-back').onclick = () => setView('src');
  $('#ro-source').onclick = () => document.dispatchEvent(new CustomEvent('ample:open-module-source'));
  $('#ro-midi-info').onclick = () => { const features = unsupportedFeatures(); void ask('Preserved MIDI data', `The node view does not display these retained MIDI events. They remain in the module source and can be inspected or edited there:\n\n${features.map((feature) => `• ${feature}`).join('\n')}`, [{ label: 'Close', value: true }]); };
}

export const refuse = (msg) => { toast(msg); return false; };
