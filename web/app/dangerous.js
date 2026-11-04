// The one dialog for dangerous acts: dry run, impact, backup suggestion, typed acceptance, reason, then the second call.
// The same dialog is used by the history views and (later) the managers. @ample:dangerous-act

import { api } from './api.js';
import { ask, el, toast } from './ui.js';

/**
 * Runs a dangerous act in two steps. `path` is the dry-run route, `body` its input.
 * Resolves true when the act was confirmed and done.
 */
export async function dangerousAct(path, body) {
  let dry;
  try {
    dry = await api.post(path, body);
  } catch (e) {
    toast(`${e.code ? `${e.code}: ` : ''}${e.message}`);
    return false;
  }
  const typed = el('input', { placeholder: `type: ${dry.acceptance}`, autocomplete: 'off' });
  const reason = el('input', { placeholder: 'why (kept in AMPLE Redux)', autocomplete: 'off' });
  const text = [...dry.impact.map((i) => `• ${i}`), '', dry.backupSuggestion, '', `Type exactly: ${dry.acceptance}`].join('\n');
  const extras = [typed, reason];
  try {
    const status = await api.get('/api/backup');
    const where = status.status && status.status.target;
    extras.push(el('div', { className: 'note', textContent: status.suggestion, style: 'width:100%;margin:0' }));
    if (where) {
      extras.push(el('button', { type: 'button', id: 'act-backup', textContent: 'Make a backup now', onclick: async (e) => { e.target.disabled = true; try { const r = await api.post('/api/backup/create', { target: where }); toast(`Backup ${r.id} made.`); } catch (x) { toast(x.message); } e.target.disabled = false; } }));
    }
  } catch { /* no backup information: the dialog works without it */ }
  for (;;) {
    const go = await ask(dry.title, text, [{ label: 'Cancel', value: false }, { label: 'Do it', value: true, primary: true }], extras);
    if (!go) return false;
    if (typed.value === dry.acceptance && reason.value.trim()) break;
    toast(`Type exactly "${dry.acceptance}" and give a reason.`);
  }
  try {
    await api.post('/api/acts/confirm', { token: dry.token, typed: typed.value, reason: reason.value.trim() });
    return true;
  } catch (e) {
    toast(`${e.code ? `${e.code}: ` : ''}${e.message}`);
    return false;
  }
}
