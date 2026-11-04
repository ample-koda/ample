// Which model and thinking level Koda works with. The default is the setting of the project (Koda's own, else the assistant's); a choice in the context menu is an override for this Koda session only
// (the next question starts with it) until "Set as default for Koda" saves it. Nothing here ever changes by itself: what Koda uses is always what the menu and the little chip say. @ample:koda-model
import { api } from './api.js';
import { powerOf } from '../lib/koda-power.mjs';
import { tx } from './koda-lang.js';
import { toast } from './ui.js';

const state = { models: [], serverDefault: '', saved: { model: '', effort: '' }, override: null, loaded: null };
const listeners = new Set();
export const onModelChange = (fn) => { listeners.add(fn); return () => listeners.delete(fn); };
const changed = () => listeners.forEach((fn) => { try { fn(); } catch { /* a listener must not break the others */ } });

/** The models Codex offers, the default of the assistant and the default of Koda (loaded once). */
export function loadModels(force = false) {
  if (state.loaded && !force) return state.loaded;
  state.loaded = (async () => {
    try {
      const [models, settings] = await Promise.all([api.get('/api/ai/models'), api.get('/api/project/settings')]);
      state.models = models.models || [];
      state.serverDefault = models.default || state.models.find((m) => m.isDefault)?.id || '';
      state.saved = { model: settings.koda?.model || '', effort: settings.koda?.effort || '' };
    } catch { /* the menu says the models could not be read */ }
    changed();
  })();
  return state.loaded;
}

const find = (id) => state.models.find((m) => m.id === id) || null;
/** What Koda starts its next question with: the override of this session, else the default of Koda (empty: the assistant's own setting). */
export const effective = () => ({ model: state.override?.model ?? state.saved.model, effort: state.override?.effort ?? state.saved.effort });
export const isOverride = () => !!state.override;
const modelOf = (e = effective()) => find(e.model || state.serverDefault);
export const currentPower = () => { const e = effective(); return powerOf(modelOf(e), e.effort); };
const effortLabel = (effort, model) => (!effort || effort === 'default' ? tx('effort.default') + (model?.defaultEffort ? ' (' + (tx('effort.' + model.defaultEffort) || model.defaultEffort) + ')' : '') : tx('effort.' + effort) || effort);
const modelName = (id) => find(id || state.serverDefault)?.name || id || state.serverDefault || tx('model.assistant');
/** The model and level for a new task: only what was chosen (nothing means the setting of the assistant). */
export const requestParts = () => { const e = effective(); return { ...(e.model ? { model: e.model } : {}), ...(e.effort ? { effort: e.effort } : {}) }; };
export const summary = () => { const e = effective(); return { model: modelName(e.model), effort: effortLabel(e.effort, modelOf(e)).replace(/ \(.*/, ''), override: isOverride() }; };

const dots = (m) => { const p = powerOf(m, 'medium'); return '●'.repeat(p.tier + 1) + '○'.repeat(4 - p.tier); };

/**
 * The items of the context menu of Koda for the model and the thinking level. busy: Koda is working now (a change counts from its next turn, the turn that runs keeps what it started with).
 * apply(model, effort): hands a change to the task that is working, so its next turn uses it.
 */
export async function menuItems({ busy = false, apply = null } = {}) {
  if (!state.models.length) await Promise.race([loadModels(true), new Promise((r) => setTimeout(r, 1500))]);
  const e = effective(), now = modelOf(e), power = currentPower();
  const afterChange = (note) => { changed(); if (busy) { toast(tx('model.next')); apply?.(effective().model, effective().effort); } else if (note) toast(note); };
  const items = [{ head: tx('model.head') }];
  const who = summary();
  items.push({ label: `${who.model} · ${who.effort}${who.override ? ' · ' + tx('model.override') : ' · ' + tx('model.defaultword')}`, title: tx('model.power', { x: tx('power.' + power.name) }), fn: () => {} });
  if (busy) items.push({ head: tx('model.busy') });
  if (!state.models.length) { items.push({ label: tx('model.none'), fn: () => loadModels(true) }); }
  if (isOverride()) items.push({ label: tx('model.usedefault'), fn: () => { state.override = null; afterChange(tx('model.cleared')); } });
  const chosenModel = e.model;
  items.push({
    label: tx('model.model'),
    children: [
      { label: (!chosenModel ? '✓ ' : '   ') + tx('model.defaultof', { x: modelName(state.saved.model) }), fn: () => { state.override = { model: '', effort: state.override?.effort ?? state.saved.effort }; if (state.override.model === state.saved.model && state.override.effort === state.saved.effort) state.override = null; afterChange(); } },
      ...state.models.map((m) => ({ label: `${chosenModel === m.id ? '✓ ' : '   '}${m.name || m.id}  ${dots(m)}`, title: m.description || '', fn: () => {
        const supported = m.efforts || [];
        const effort = state.override?.effort ?? state.saved.effort;
        state.override = { model: m.id, effort: effort && effort !== 'default' && supported.length && !supported.includes(effort) ? '' : effort };
        afterChange();
      } })),
    ],
  });
  const efforts = (now?.efforts?.length ? now.efforts : ['low', 'medium', 'high', 'xhigh']).filter((x) => x && x !== 'none');
  items.push({
    label: tx('model.thinking') + ' · ' + effortLabel(e.effort, now).replace(/ \(.*/, ''),
    children: [
      { label: (!e.effort || e.effort === 'default' ? '✓ ' : '   ') + effortLabel('', now), fn: () => { state.override = { model: state.override?.model ?? state.saved.model, effort: '' }; afterChange(); } },
      ...efforts.map((x) => ({ label: (e.effort === x ? '✓ ' : '   ') + (tx('effort.' + x) || x), fn: () => { state.override = { model: state.override?.model ?? state.saved.model, effort: x }; afterChange(); } })),
    ],
  });
  if (isOverride()) {
    items.push({ label: tx('model.setdefault'), title: tx('model.setdefault.title'), fn: async () => {
      const o = state.override;
      try {
        await api.post('/api/project/settings', { koda: { model: o.model, effort: o.effort } });
        state.saved = { model: o.model, effort: o.effort };
        state.override = null;
        changed();
        toast(tx('model.saved'));
      } catch (error) { toast(error.message || String(error)); }
    } });
  }

  return items;
}
