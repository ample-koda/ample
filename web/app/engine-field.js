// The engine parameter of the ACE blocks in the Parameters panel (plan engine-per-block): a place (the project default, local, Faer, Faer else local) and, for ace-music and ai-voice, a model
// (a family or a model file the servers list). It writes `engine "route:model"` and tells beforehand what would stop the build. @ample:engine
import { api } from './api.js';
import { el } from './ui.js';
import { engineProblem, formatEngine, parseEngine } from '../lib/engine-choice.mjs';

const FAMILIES = ['default', 'turbo', 'sft', 'base', 'xl-turbo', 'xl-sft', 'xl-base'];
const ROUTES = [['', 'Project default'], ['local', 'Local ACE server'], ['faer', 'Faer (remote GPU)'], ['faer-else-local', 'Faer, else local']];

let cache = null, cacheAt = 0;
/** The model files and the Faer state, read at most every ten seconds (the panel is drawn again after every edit). */
async function servers() {
  if (cache && Date.now() - cacheAt < 10000) return cache;
  const [models, faer] = await Promise.all([api.get('/api/ace/models').catch(() => null), api.get('/api/faer/status').catch(() => null)]);
  cache = { models: models?.models ?? [], faer }; cacheAt = Date.now();
  return cache;
}

/**
 * ctx: { blockType, value, disabled, title, commit(text) }. The text handed to commit is the value without quotes ('' removes the parameter).
 */
export function engineField({ blockType, value, disabled, title, commit }) {
  const current = parseEngine(value);
  const route = el('select', { className: 'engine-route', disabled, title: title || 'Where this block is rendered; the project default when empty', 'aria-label': 'Engine place' }, ROUTES.map(([v, label]) => el('option', { value: v, textContent: label })));
  route.value = ROUTES.some(([v]) => v === current.route) ? current.route : '';
  const takesModel = blockType !== 'voice-clone';
  const listId = `engine-models-${Math.random().toString(36).slice(2, 8)}`;
  const list = el('datalist', { id: listId }, FAMILIES.map((family) => el('option', { value: family })));
  const model = el('input', { className: 'engine-model', value: current.model, placeholder: 'model: the song or the project decides', disabled: disabled || !route.value, title: route.value ? 'A family (turbo, sft, base, xl-turbo, xl-sft, xl-base) or a model file the servers list' : 'Choose a place first: a model needs one', 'aria-label': 'Engine model', list: listId });
  const note = el('div', { className: 'hint engine-note', role: 'status' });
  const write = () => commit(formatEngine(route.value, takesModel ? model.value.trim() : ''));
  route.onchange = () => { model.disabled = disabled || !route.value; if (!route.value) model.value = ''; write(); };
  model.onchange = write;
  servers().then((state) => {
    for (const m of state.models.filter((x) => x.role === 'dit')) list.append(el('option', { value: m.file }));
    const paint = () => { note.textContent = engineProblem({ blockType, route: route.value, model: model.value.trim(), state }); note.hidden = !note.textContent; };
    paint(); route.addEventListener('change', paint); model.addEventListener('input', paint);
  });
  return el('div', { className: 'engine-field' }, [el('div', { className: 'engine-row' }, takesModel ? [route, model, list] : [route]), note]);
}
