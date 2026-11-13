// The projects manager (header button): recent projects with their health and backup age, a new-project form, the project
// settings, the health page (findings, black box, interrupted commands, backups) and backups and export. @ample:projects
// @ample:project-settings @ample:health @ample:export @ample:cleanup

import { api } from './api.js';
import { $, ask, el, toast } from './ui.js';
import { S, loadHistory, stopBuild } from './store.js';
import { dangerousAct } from './dangerous.js';
import { seedDice } from './seed-dice.js';
import { storagePage } from './storage.js';
import { finalsPage } from './finals.js';

const PM = { page: 'recent', open: false };
const PAGES = [['recent', 'Recent projects'], ['new', 'New project'], ['settings', 'Project settings'], ['health', 'Health'], ['finals', 'Finals'], ['storage', 'Storage'], ['backup', 'Backups and export']];
const mib = (n) => `${(n / 1048576).toFixed(0)} MiB`;

export function openProjects(page = 'recent') {
  PM.page = page;
  PM.open = true;
  $('#pm').style.display = 'grid';
  render();
}

export function closeProjects() { PM.open = false; dirty.clear(); $('#pm').style.display = 'none'; }

// Unsaved edits on the settings tabs (tab id -> true); leaving the page asks first.
const dirty = new Set();
let carry = null;
const TAB_LABELS = { general: 'General', audio: 'Audio routing', search: 'Web search', saves: 'Safe saves', assistant: 'Assistant', seed: 'Seed and folders' };

/** True when it is fine to leave the current page: nothing unsaved, or the user chose to discard it. */
async function leaveOk() {
  if (PM.page !== 'settings' || !dirty.size) return true;
  const names = [...dirty].map((id) => TAB_LABELS[id] || id).join(', ');
  const discard = await ask('Leave without saving?', `These settings tabs have changes that are not saved: ${names}.`, [{ label: 'Keep editing', value: false, primary: true }, { label: 'Discard changes', value: true }]);
  if (discard) dirty.clear();
  return !!discard;
}

async function requestClose() { if (await leaveOk()) closeProjects(); }

async function render() {
  if (!PM.open) return;
  const root = $('#pm');
  root.innerHTML = '';
  const nav = el('div', { className: 'pmnav' });
  nav.append(el('button', { id: 'pm-close-top', className: 'primary', textContent: '← Back to the song', title: 'Close the projects view (Esc)', onclick: requestClose }), el('h4', { textContent: `Projects · ${S.project ? S.project.name : ''}` }));
  PAGES.forEach(([k, l]) => { const b = el('button', { textContent: l, className: PM.page === k ? 'on' : '', onclick: async () => { if (!(await leaveOk())) return; PM.page = k; render(); } }); b.dataset.page = k; nav.append(b); });
  nav.append(el('span', { className: 'sp' }), el('button', { id: 'pm-close', textContent: '← Back to the song', onclick: requestClose }));
  const body = el('div', { className: 'pmbody', id: 'pm-body' });
  root.append(nav, body);
  try {
    await ({ recent, new: newProject, settings, health, finals: finalsPage, storage: storagePage, backup }[PM.page])(body);
  } catch (e) { body.append(el('p', { className: 'seerr error', textContent: `${e.code ? `${e.code}: ` : ''}${e.message}` })); }
}

async function recent(body) {
  body.append(el('h2', { textContent: 'Recent projects' }), el('p', { className: 'note', textContent: 'Open another project in a new tab (it gets its own server, address and session token); this tab and its song keep running, so you can close the new tab and come back here. Use Back to the song to return.' }));
  const { projects } = await api.get('/api/projects');
  const grid = el('div', { className: 'pmgrid' });
  projects.forEach((p) => {
    const c = el('div', { className: 'card' });
    c.dataset.project = p.name;
    c.append(el('div', {}, [el('b', { textContent: p.name }), p.current ? el('span', { className: 'tag ok', textContent: 'open here', style: 'margin-left:6px' }) : '']), el('div', { className: 'note', textContent: p.path, style: 'word-break:break-all' }));
    const badges = el('div', { className: 'row' });
    badges.append(el('span', { className: `tag ${p.reachable ? 'ok' : 'missing'}`, textContent: p.reachable ? 'found' : 'folder missing' }));
    if (p.findings) badges.append(el('span', { className: 'tag missing', textContent: `${p.findings} problem(s)` }));
    if (p.warnings) badges.append(el('span', { className: 'tag peek', textContent: `${p.warnings} warning(s)` }));
    badges.append(el('span', { className: 'tag', textContent: p.backupAgeDays === null ? 'no backup' : `backup ${p.backupAgeDays} d old` }));
    const act = el('div', { className: 'row' });
    if (!p.current && p.reachable) act.append(el('button', { textContent: 'Open', className: 'primary', onclick: async () => { try { const r = await api.post('/api/projects/open', { folder: p.path }); window.open(r.url, '_blank'); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); } } }));
    if (!p.current) act.append(el('button', { textContent: 'Forget', title: 'Remove from this list (the folder is not touched)', onclick: async () => { try { await api.post('/api/projects/forget', { folder: p.path }); render(); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}Could not forget the project: ${e.message}`); } } }));
    c.append(badges, act);
    grid.append(c);
  });
  body.append(grid);
}

async function newProject(body) {
  body.append(el('h2', { textContent: 'New project' }));
  const name = el('input', { id: 'pm-new-name', placeholder: 'name' });
  const folder = el('input', { id: 'pm-new-folder', placeholder: 'a new folder, for example D:\\Music\\my-song' });
  body.append(el('div', { className: 'prop' }, [el('label', { textContent: 'name' }), name]), el('div', { className: 'prop' }, [el('label', { textContent: 'folder' }), folder]), el('p', { className: 'note', textContent: 'The folder is created with the mandatory .ample structure (history, take bank, AMPLE Redux archive). Nothing is overwritten: an existing project folder is refused.' }));
  body.append(el('button', { id: 'pm-new-create', className: 'primary', textContent: 'Create', onclick: async () => {
    if (!folder.value.trim()) { toast('Choose a folder.'); return; }
    try { const r = await api.post('/api/projects/new', { folder: folder.value.trim(), name: name.value.trim() || undefined }); toast(`Created ${r.name}. Open it from the recent list.`); PM.page = 'recent'; render(); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); }
  } }));
}

let settingsTab = 'general';

async function settings(body) {
  const s = await api.get('/api/project/settings');
  dirty.clear();
  const restore = carry; carry = null;
  body.append(el('h2', { textContent: 'Project settings' }), el('p', { className: 'note', textContent: `${s.name}. Project settings are saved with the project and journaled in AMPLE Redux; API keys stay encrypted in this Windows user profile.` }));
  // The settings are split in tabs; every tab has its own Save that stores only what is on that tab.
  const TABS = [['general', 'General'], ['audio', 'Audio routing'], ['search', 'Web search'], ['saves', 'Safe saves'], ['assistant', 'Assistant'], ['seed', 'Seed and folders']];
  const panels = {}, paints = {};
  const tabBar = el('div', { className: 'ps-tabs', role: 'tablist', 'aria-label': 'Project settings' });
  const show = (id) => {
    settingsTab = id;
    for (const [tab] of TABS) {
      const on = tab === id;
      panels[tab].hidden = !on;
      const b = tabBar.querySelector(`[data-tab="${tab}"]`);
      b.classList.toggle('on', on); b.setAttribute('aria-selected', String(on)); b.tabIndex = on ? 0 : -1;
    }
  };
  for (const [id, label] of TABS) {
    panels[id] = el('div', { className: 'ps-panel', id: `ps-tab-${id}`, role: 'tabpanel', 'aria-label': label });
    tabBar.append(el('button', { type: 'button', id: `ps-tabbtn-${id}`, role: 'tab', 'aria-controls': `ps-tab-${id}`, 'data-tab': id, textContent: label, onclick: () => show(id), onkeydown: (e) => {
      const at = TABS.findIndex(([tab]) => tab === id);
      const to = e.key === 'ArrowRight' ? (at + 1) % TABS.length : e.key === 'ArrowLeft' ? (at - 1 + TABS.length) % TABS.length : e.key === 'Home' ? 0 : e.key === 'End' ? TABS.length - 1 : -1;
      if (to < 0) return;
      e.preventDefault(); const next = TABS[to][0]; show(next); tabBar.querySelector(`[data-tab="${next}"]`).focus();
    } }));
    panels[id].setAttribute('aria-labelledby', `ps-tabbtn-${id}`);
    const paint = () => {
      const b = tabBar.querySelector(`[data-tab="${id}"]`);
      const mark = b.querySelector('.ps-dirty');
      if (dirty.has(id)) { if (!mark) b.append(el('span', { className: 'ps-dirty', textContent: ' ●', 'aria-hidden': 'true' })); b.title = 'Unsaved changes'; } else { mark?.remove(); b.removeAttribute('title'); }
    };
    paints[id] = paint;
    const touch = () => { dirty.add(id); paint(); };
    panels[id].addEventListener('input', touch);
    panels[id].addEventListener('change', touch);
  }
  body.append(tabBar, ...Object.values(panels));
  let panel = panels.general;
  // After a save only the edits of the OTHER tabs are carried over into the rebuilt page; this tab shows what was stored.
  const rerender = (savedTab) => {
    carry = [];
    for (const [tab] of TABS) if (tab !== savedTab && dirty.has(tab)) panels[tab].querySelectorAll('input[id], select[id]').forEach((n) => carry.push([n.id, n.type === 'checkbox' ? n.checked : n.value]));
    render();
  };
  const saveBar = (id, label, work) => {
    const tab = Object.keys(panels).find((k) => panels[k] === panel);
    panel.append(el('button', { id, className: 'primary ps-save', type: 'button', textContent: label, onclick: async () => {
      try { await work(); dirty.delete(tab); paints[tab](); toast(`Saved: ${TAB_LABELS[tab] || tab}.`); rerender(tab); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); }
    } }));
  };
  const row = (label, node, note) => { const d = el('div', { className: 'prop ps-row' }); d.append(el('label', { textContent: label }), node); panel.append(d); if (note) panel.append(el('div', { className: 'note ps-row-note', textContent: note })); };
  const budget = el('input', { id: 'ps-budget', type: 'number', min: 1, value: Math.round(s.blackBox.budgetBytes / 1048576) });
  const full = el('input', { id: 'ps-full', type: 'number', min: 1, value: s.blackBox.fullDetailDays });
  const thin = el('input', { id: 'ps-thin', type: 'number', min: 1, value: s.blackBox.thinnedDays });
  const engine = el('input', { id: 'ps-engine', value: s.defaultEngine || '', placeholder: 'none' });
  row('AMPLE Redux budget (MiB)', budget, 'Explicit archive compaction uses this budget; records are never pruned automatically or journaling switched off.');
  row('full detail (days)', full, 'Records of this age keep their pre-images.');
  row('thinned until (days)', thin, 'Older records are thinned; the hash chain stays intact.');
  row('default engine', engine, 'Stored with the project. A block or song that names its own engine always wins. Empty clears it.');
  // how many blocks a build of this project renders at once: empty follows the server (one per core), a number can only be lower than the limit of the server
  const limit = s.parallelismLimit ?? 1;
  const parallelism = el('input', { id: 'ps-parallelism', type: 'number', min: 1, max: limit, step: 1, value: s.buildParallelism ?? '', placeholder: `server: ${limit}` });
  row('render in parallel (blocks)', parallelism, `${s.buildParallelism > limit ? `Saved: ${s.buildParallelism}, effective on this machine: ${limit}. ` : ''}How many blocks of a build render at the same time. Empty follows the server: ${limit} (this machine has ${s.cores ?? limit} cores). A number is at most ${limit}; it changes how fast a build is, never what it makes.`);
  const kodaLang = el('select', { id: 'ps-koda-language' }, [el('option', { value: 'en', textContent: 'English' }), el('option', { value: 'nl', textContent: 'Nederlands' })]);
  kodaLang.value = s.koda?.language === 'nl' ? 'nl' : 'en';
  row('Koda language', kodaLang, "The language of Koda's own words and of its answers (Dutch answers keep the English AMPLE term in brackets). Saved with the project.");
  const previewTabs = el('input', { id: 'ps-tabs-preview', type: 'checkbox', checked: localStorage.getItem('ample.tabs.preview') === '1', onchange: (e) => { e.stopPropagation(); try { localStorage.setItem('ample.tabs.preview', e.target.checked ? '1' : '0'); } catch { /* storage may be blocked */ } }, oninput: (e) => e.stopPropagation() });
  row('Preview tabs', el('label', {}, [previewTabs, ' a single click in the Project panel opens a preview tab']), 'Off: a file opens in a tab with a double click. On: a single click opens a tab in italics that the next single click replaces; a double click or an edit keeps it. Kept in this browser.');
  saveBar('ps-save', 'Save general', async () => {
    // one request, only the fields that changed (a saved value the server now caps, for example parallelism, must not block unrelated edits)
    const patch = {};
    if (Number(budget.value) !== Math.round(s.blackBox.budgetBytes / 1048576)) patch.blackBoxBudget = Number(budget.value) * 1048576;
    if (Number(full.value) !== s.blackBox.fullDetailDays) patch.fullDetailDays = Number(full.value);
    if (Number(thin.value) !== s.blackBox.thinnedDays) patch.thinnedDays = Number(thin.value);
    if (engine.value.trim() !== (s.defaultEngine || '')) patch.defaultEngine = engine.value.trim();   // empty clears it
    if (parallelism.value !== String(s.buildParallelism ?? '')) patch.buildParallelism = parallelism.value === '' ? null : Number(parallelism.value);
    const langNow = s.koda?.language === 'nl' ? 'nl' : 'en';
    if (kodaLang.value !== langNow) patch.koda = { language: kodaLang.value };
    if (Object.keys(patch).length) await api.post('/api/project/settings', patch);
    if (patch.koda) (await import('./koda-lang.js')).useKodaLanguage(kodaLang.value);
  });
  panel = panels.search;
  const braveKey = el('input', { id: 'ps-brave-key', type: 'password', autocomplete: 'new-password', placeholder: s.braveSearchKeyConfigured ? 'Configured; leave empty to keep' : 'Paste Brave Search API key', disabled: !s.braveSearchKeyStoreAvailable });
  const clearBraveKey = el('input', { id: 'ps-brave-clear', type: 'checkbox' });
  row('Brave Search API key', braveKey, s.braveSearchKeyStoreAvailable ? (s.braveSearchKeyConfigured ? 'A project-specific key is stored encrypted in this Windows user profile. It is never returned to the page or stored in the project folder.' : 'Optional. DuckDuckGo remains available without a key.') : 'Project key storage requires Windows DPAPI. On other systems, configure the server environment variable instead.');
  row('remove saved key', el('label', {}, [clearBraveKey, ' Clear project key']), 'A server-wide environment key may still be used as a fallback.');
  saveBar('ps-save-search', 'Save web search', () => api.post('/api/project/settings', { braveSearchApiKey: braveKey.value.trim(), clearBraveSearchApiKey: clearBraveKey.checked }));
  panel = panels.audio;
  panel.append(el('h4', { textContent: 'Faer (remote GPU) audio', style: 'padding:0;margin:0 0 4px' }));
  const route = (id, value, either) => { const select = el('select', { id }, [el('option', { value: 'local', textContent: 'Local ACE server', title: 'ACE-Step running on this machine' }), el('option', { value: 'faer', textContent: 'Faer (remote GPU)', title: 'Faer is the remote GPU service' }), ...(either ? [el('option', { value: 'faer-else-local', textContent: 'Faer, else local ACE server', title: 'Use Faer (remote GPU) when it is ready, otherwise the local ACE server' })] : [])]); select.value = value; return select; };
  const song = route('ps-faer-song', s.faer?.song || 'local', true);
  const voice = route('ps-faer-voice', s.faer?.voice || 'local', true);
  const separation = route('ps-faer-separation', s.faer?.separation || 'local');
  row('song generation', song, 'ACE music can run on the configured Faer server. "Faer, else local" uses Faer when it is connected and ready, otherwise the local ACE-Step server (started when needed).');
  row('voice generation', voice, 'ACE voice and voice-clone require their remote workers to be ready.');
  row('stem separation', separation, 'Requires a Faer separation worker.');
  // the default ACE models of the project: a family or a .gguf file; a block can still choose its own with its engine parameter (for example engine "faer:xl-sft")
  const modelList = el('datalist', { id: 'ps-model-list' }, ['default', 'turbo', 'sft', 'base', 'xl-turbo', 'xl-sft', 'xl-base'].map((family) => el('option', { value: family })));
  api.get('/api/ace/models').then((models) => (models.models || []).filter((m) => m.role === 'dit').forEach((m) => modelList.append(el('option', { value: m.file })))).catch(() => {});
  const songModel = el('input', { id: 'ps-faer-song-model', value: s.faer?.songModel || '', placeholder: 'the song or the plugin decides', list: 'ps-model-list' });
  const voiceModel = el('input', { id: 'ps-faer-voice-model', value: s.faer?.voiceModel || '', placeholder: 'the song or the plugin decides', list: 'ps-model-list' });
  row('default song model', songModel, 'The ACE model for ace-music blocks when neither the block (engine "faer:xl-sft") nor the song settings (acestep.synthModel) name one: a family (turbo, sft, base, xl-turbo, xl-sft, xl-base) or a .gguf file name.');
  row('default voice model', voiceModel, 'The same for ai-voice blocks. A voice-clone block chooses its voice model with its own model parameter.');
  panel.append(modelList);
  panel.append(el('p', { className: 'note', textContent: 'These choices belong to this project. The server address and token stay in this machine’s Faer profile. Unavailable remote capabilities fail before rendering.' }));
  const readiness = el('p', { className: 'note', textContent: 'Checking Faer (remote GPU) connection...' });
  panel.append(readiness);
  api.get('/api/faer/status').then((remote) => {
    if (!remote.configured) readiness.textContent = 'Faer connection: no machine-local profile.';
    else if (!remote.connected) readiness.textContent = 'Faer connection: unavailable.';
    else { const capabilities = remote.status?.capabilities || {}; readiness.textContent = `Faer connection: ready. ACE song ${capabilities.song?.ready ? 'available' : 'unavailable'}; ACE voice ${capabilities['voice-ace']?.ready ? 'available' : 'unavailable'}; voice clone ${capabilities['voice-clone']?.ready ? 'available' : 'unavailable'}; separation ${capabilities.separation?.ready ? 'available' : 'unavailable'}.`; }
  }).catch(() => { readiness.textContent = 'Faer connection: status check failed.'; });
  saveBar('ps-save-audio', 'Save audio routing', async () => {
    const changed = song.value !== (s.faer?.song || 'local') || voice.value !== (s.faer?.voice || 'local') || separation.value !== (s.faer?.separation || 'local') || songModel.value.trim() !== (s.faer?.songModel || '') || voiceModel.value.trim() !== (s.faer?.voiceModel || '');
    await api.post('/api/project/settings', { faer: { song: song.value, voice: voice.value, separation: separation.value, songModel: songModel.value.trim(), voiceModel: voiceModel.value.trim() } });
    if (changed) stopBuild();   // only after the server accepted the routing: a rejected edit must not cancel a running build
  });
  // safe saves: a checkpoint before and after every save, and an autosave every few minutes (kept in .ample/auto-checkpoint.json, not in project.json)
  const auto = await api.get('/api/project/auto-checkpoint');
  panel = panels.saves;
  const on = el('input', { id: 'ps-auto-on', type: 'checkbox', checked: auto.enabled });
  const slider = el('input', { id: 'ps-auto-minutes', type: 'range', min: 0, max: auto.steps.length - 1, step: 1, value: auto.steps.reduce((best, step, i) => (Math.abs(step - auto.minutes) < Math.abs(auto.steps[best] - auto.minutes) ? i : best), 0), disabled: !auto.enabled });
  const label = (minutes) => (minutes < 60 ? `${minutes} min` : `${minutes / 60} h`);
  const shown = el('span', { id: 'ps-auto-shown', className: 'ps-slider-value', textContent: label(auto.steps[Number(slider.value)]) });
  const ticks = el('div', { className: 'ps-ticks', 'aria-hidden': 'true' }, auto.steps.map((minutes) => el('span', { textContent: label(minutes) })));
  on.onchange = () => { slider.disabled = !on.checked; };
  slider.oninput = () => { shown.textContent = label(auto.steps[Number(slider.value)]); };
  row('safe saves', el('label', {}, [on, ' on']), 'Every save gets a checkpoint of the files as they were and one of what was saved, named after what changed. Checkpoints are listed in History.');
  row('autosave every', el('div', { className: 'ps-slider' }, [el('div', { className: 'ps-slider-line' }, [slider, shown]), ticks]), 'Changes that are not saved are saved on their own after this time (the files as they were get a checkpoint first). Runs on the server, also when the browser is closed.');
  saveBar('ps-save-saves', 'Save safe saves', () => api.post('/api/project/auto-checkpoint', { enabled: on.checked, minutes: auto.steps[Number(slider.value)] }));
  panel = panels.assistant;
  row('assistant', el('button', { id: 'ps-ai', type: 'button', textContent: 'Configure...', onclick: () => import('./ai.js').then((m) => m.openAiSettings()) }), 'Optional: Codex can propose changes (a sequence, code, comments). Which Codex identity this project uses and how much freedom Codex has is set here; what is allowed at all is set by the server. Nothing changes until you accept a proposal.');
  const exposeTakebank = el('input', { id: 'ps-ai-takebank', type: 'checkbox', checked: s.ai?.exposeTakebankCache !== false });
  row('read-only takebank cache', el('label', {}, [exposeTakebank, ' expose exact-plan cached audio to AI proposal builds']), 'On by default. Matching takes are read from this project and copied into the temporary proposal cache; the project takebank is never written.');
  saveBar('ps-save-assistant', 'Save assistant', () => api.post('/api/project/settings', { exposeTakebankCache: exposeTakebank.checked }));
  panel = panels.seed;
  panel.append(el('h4', { textContent: 'Master seed', style: 'padding:0;margin:0 0 4px' }));
  const seed = el('input', { id: 'ps-seed', value: String(s.masterSeed), disabled: true });
  row('master seed', seed, 'Seeds of blocks that have none of their own are derived from it. Changing it is a dangerous act: the takes made from the old seeds cannot be made again from the new one.');
  const next = el('input', { id: 'ps-newseed', placeholder: 'a new whole number', inputMode: 'numeric' });
  row('new master seed', el('span', { className: 'seed-row' }, [next, seedDice(() => next)]));
  panel.append(el('button', { id: 'ps-changeseed', textContent: 'Change the master seed...', onclick: async () => {
    if (!/^\d{1,20}$/.test(next.value.trim())) { toast('A seed is a whole number.'); return; }
    if (await dangerousAct('/api/acts/master-seed', { seed: next.value.trim() })) { toast('Master seed changed; it takes effect the next time the project is opened.'); dirty.delete('seed'); rerender('seed'); }
  } }));
  panel.append(el('h4', { textContent: 'Linked folders', style: 'padding:0;margin:16px 0 4px' }), el('p', { className: 'note', textContent: `${s.mounts.length} project link(s). Manage them in the library manager.` }));
  if (restore) for (const [id, value] of restore) {
    const n = body.querySelector(`#${id}`);
    if (!n) continue;
    if (n.type === 'checkbox') n.checked = value; else n.value = value;
    n.dispatchEvent(new Event('input', { bubbles: true })); n.dispatchEvent(new Event('change', { bubbles: true }));
  }
  show(settingsTab);
}

/** The settings pages for the first-time wizard: builds them into `body` (kept out of sight) and gives the wizard the panel of each tab and a save that says whether it worked. */
export async function settingsForWizard(body) {
  await settings(body);
  const panels = new Map([...body.querySelectorAll('.ps-panel')].map((panel) => [panel.id.replace('ps-tab-', ''), panel]));   // kept here: the wizard moves them out of `body`
  const panelOf = (id) => panels.get(id);
  return {
    panel: panelOf,
    async save(id) {
      const button = panelOf(id)?.querySelector('.ps-save');
      if (!button || !dirty.has(id)) return true;   // nothing changed on that page: nothing is written
      await button.onclick();
      return !dirty.has(id);
    },
  };
}

async function health(body) {
  const [h, redux] = await Promise.all([api.get('/api/health'), api.get('/api/redux/status')]);
  body.append(el('h2', { textContent: 'Health' }));
  const sec = (title) => { const t = el('h4', { textContent: title, style: 'padding:0;margin:14px 0 4px' }); body.append(t); };
  sec('Project folder');
  if (!h.findings.length) body.append(el('p', { className: 'note', id: 'hl-ok', textContent: 'No findings: the structure, the lock, the format version and the disk space are fine.' }));
  h.findings.forEach((f) => body.append(el('div', { className: `seerr ${f.severity === 'error' ? 'error' : f.severity === 'warning' ? 'warning' : 'info'}`, textContent: `${f.code} ${f.message}${f.repair ? `  (${f.repair})` : ''}` })));
  sec('AMPLE Redux');
  body.append(el('p', { className: `note${redux.state === 'ok' ? '' : ' warning'}`, id: 'hl-blackbox', textContent: `${h.blackBox.badSeq ? `AR-${h.blackBox.badSeq} is damaged or changed (${h.blackBox.records} good before it).` : `${h.blackBox.records} AMPLE Redux record(s), hash chain intact${h.blackBox.tornTail ? ' (the last line was torn by a crash and is ignored)' : ''}.`} Storage: ${(redux.usedBytes / 1073741824).toFixed(2)} GiB of ${(redux.budgetBytes / 1073741824).toFixed(2)} GiB; warning at ${(redux.warnAtBytes / 1073741824).toFixed(2)} GiB. Compaction is explicit; nothing is deleted automatically.` }));
  body.append(el('button', { id: 'hl-redux-compact', textContent: 'Preview compaction...', onclick: async () => {
    try {
      const preview = await api.get('/api/redux/compact-preview');
      if (!preview.segmentsToThin && !preview.summariesToDelete) { toast('AMPLE Redux has nothing to compact.'); return; }
      const impact = `This will thin ${preview.segmentsToThin} segment(s) containing ${preview.recordsToThin} record(s), and remove ${preview.summariesToDelete} expired summary(s). Storage: ${(preview.bytesBefore / 1073741824).toFixed(2)} GiB → ${(preview.bytesAfter / 1073741824).toFixed(2)} GiB.`;
      if (await ask('Compact AMPLE Redux?', impact, [{ label: 'Cancel', value: false }, { label: 'Compact archive', value: true, primary: true }])) { const result = await api.post('/api/redux/compact', { expectedSequence: preview.throughSequence }); toast(`Compacted ${result.thinned} segment(s); ${result.deleted} summary(s) removed.`); render(); }
    } catch (error) { toast(error.message || String(error)); }
  } }));
  sec('Interrupted commands');
  if (!h.interrupted.length) body.append(el('p', { className: 'note', id: 'hl-interrupted', textContent: 'Nothing was interrupted.' }));
  h.interrupted.forEach((i) => {
    const row = el('div', { className: 'seerr warning', style: 'cursor:default' });
    row.dataset.seq = i.seq;
    row.append(el('div', { textContent: `▲ record ${i.seq}: ${i.kind} ${i.payloadJson}` }), el('div', { className: 'row' }, [
      el('button', { textContent: 'Restore what it was about to overwrite', onclick: async () => {
        if (!await ask('Restore the old version?', 'The file goes back to what it was before the interrupted command. What is in the file now is overwritten.', [{ label: 'Cancel', value: false }, { label: 'Restore', value: true, primary: true }])) return;
        try { await api.post('/api/health/recover', { seq: i.seq, action: 'restore' }); toast('Restored from the pre-image.'); render(); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}Could not restore: ${e.message}`); }
      } }),
      el('button', { textContent: 'Accept the file as it is', onclick: async () => { try { await api.post('/api/health/recover', { seq: i.seq, action: 'accept' }); toast('Marked as dealt with.'); render(); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}Could not mark it: ${e.message}`); } } }),
    ]));
    body.append(row);
  });
  sec('Regeneration');
  body.append(el('p', { className: 'note', textContent: (h.pendingRegens || []).length ? `${h.pendingRegens.length} regeneration decision(s) are waiting: select the block and decide in its controls.` : 'No regeneration is waiting for a decision.' }));
  sec('Project log');
  const extra = await api.get('/api/ai/grants').catch(() => ({ grants: [], log: [] }));
  if (!extra.log.length) body.append(el('p', { className: 'note', id: 'hl-log', textContent: 'Nothing is missing for the tools of this project. "ample doctor --env" checks what the assistant needs around the program.' }));
  extra.log.forEach((e) => body.append(el('div', { className: `seerr ${e.kind === 'missing' ? 'warning' : 'info'}`, 'data-log': e.what, textContent: `${e.what}: ${e.hint}` })));
  sec('Permissions for the assistant');
  if (!extra.grants.length && !extra.browserAccess) body.append(el('p', { className: 'note', id: 'hl-grants', textContent: 'The assistant may write nowhere outside the project. When it asks (or is stopped), you can allow a place for this project; it is listed here.' }));
  if (extra.browserAccess) body.append(el('div', { className: 'row', 'data-grant': 'visible-browser' }, [
    el('span', { textContent: 'Visible browser access (always allowed in this project)' }),
    el('button', { textContent: 'Revoke', onclick: async () => { await api.post('/api/ai/grants/revoke', { browserAccess: true }); toast('Browser permission taken back.'); render(); } }),
  ]));
  extra.grants.forEach((g) => body.append(el('div', { className: 'row', 'data-grant': g.path }, [
    el('span', { textContent: `${g.path} (${g.kind === 'passthrough' ? 'only through AMPLE commands' : 'writing allowed'})` }),
    el('button', { textContent: 'Revoke', onclick: async () => { await api.post('/api/ai/grants/revoke', { path: g.path }); toast('Permission taken back.'); render(); } }),
  ])));
  sec('Backup');
  const b = h.backup;
  body.append(el('p', { className: 'note', id: 'hl-backup', textContent: b && b.id ? `The last backup is ${b.ageDays < 1 ? 'from today' : `${Math.floor(b.ageDays)} day(s) old`} (${b.id}) in ${b.target}.` : 'There is no backup of this project yet.' }));
  body.append(el('button', { textContent: 'Backups and export', onclick: () => { PM.page = 'backup'; render(); } }));
}

async function backup(body) {
  const info = await api.get('/api/backup');
  body.append(el('h2', { textContent: 'Backups and export' }), el('p', { className: 'note', id: 'bk-suggestion', textContent: info.suggestion }));
  const target = el('input', { id: 'bk-target', value: info.status ? info.status.target : '', placeholder: 'a folder on another drive, for example E:\\ample-backups' });
  body.append(el('div', { className: 'prop pm-row' }, [el('label', { textContent: 'backup folder' }), target]));
  const auto = Object.assign(el('input', { id: 'bk-auto', type: 'checkbox', checked: !!(info.status && info.status.auto), style: 'width:auto' }), { onchange: async () => { if (!target.value.trim()) { toast('Choose the backup folder first.'); auto.checked = false; return; } await api.post('/api/backup/policy', { target: target.value.trim(), auto: auto.checked }); toast(auto.checked ? 'A backup is made before every dangerous act (from the next start of the server).' : 'Off.'); } });
  body.append(el('label', {}, [auto, ' make a backup before every dangerous act']));
  const noTakes = el('input', { id: 'bk-notakes', type: 'checkbox', style: 'width:auto' });
  body.append(el('div', { className: 'row' }, [el('button', { id: 'bk-create', className: 'primary', textContent: 'Back up now', onclick: async () => {
    if (!target.value.trim()) { toast('Choose the backup folder first.'); return; }
    try { const r = await api.post('/api/backup/create', { target: target.value.trim(), includeTakes: !noTakes.checked }); toast(`Backup ${r.id}: ${r.newObjects} new file(s), ${r.milliseconds} ms.`); render(); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); }
  } }), el('label', {}, [noTakes, ' leave out the takes']), el('button', { id: 'bk-verify', textContent: 'Verify the latest', onclick: async () => { try { const r = await api.post('/api/backup/verify', { target: target.value.trim() || undefined }); toast(r.ok ? `Backup ${r.id} is intact; it is now the last good one.` : `Backup ${r.id}: ${r.missing.length} missing, ${r.corrupt.length} damaged.`); render(); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); } } })]));
  const list = el('div', { id: 'bk-list' });
  (info.backups || []).forEach((m) => {
    const r = el('div', { className: 'row' });
    r.dataset.backup = m.id;
    r.append(el('span', { textContent: `${m.id}  ${m.files} files  ${mib(m.bytes)}${m.includesTakes ? '' : '  (no takes)'}` }), el('button', { textContent: 'Restore into this project...', onclick: async () => { if (await dangerousAct('/api/acts/restore-in-place', { target: target.value.trim(), id: m.id })) { toast('Restored.'); location.reload(); } } }));
    list.append(r);
  });
  if (!(info.backups || []).length) list.append(el('p', { className: 'note', textContent: 'No backups yet.' }));
  body.append(el('h4', { textContent: 'Backups', style: 'padding:0;margin:14px 0 4px' }), list, el('div', { className: 'row' }, [el('button', { id: 'bk-prune', textContent: 'Prune old backups...', onclick: async () => { if (!target.value.trim()) { toast('Choose the backup folder first.'); return; } if (await dangerousAct('/api/acts/prune-backups', { target: target.value.trim(), keepLast: 5 })) { toast('Pruned.'); render(); } } })]));
  body.append(el('h4', { textContent: 'Export', style: 'padding:0;margin:18px 0 4px' }), el('p', { className: 'note', textContent: 'One file that any zip tool can read: the project without secrets, with a manifest of hashes. Import checks every hash.' }));
  const file = el('input', { id: 'ex-file', placeholder: 'file to write, for example D:\\song.ample' });
  const exNo = el('input', { id: 'ex-notakes', type: 'checkbox', style: 'width:auto' });
  body.append(el('div', { className: 'prop pm-row' }, [el('label', { textContent: 'archive file' }), file]), el('div', { className: 'row' }, [el('button', { id: 'ex-go', textContent: 'Export', onclick: async () => { if (!file.value.trim()) { toast('Choose a file.'); return; } try { const r = await api.post('/api/export', { file: file.value.trim(), includeTakes: !exNo.checked }); toast(`Exported ${r.files} files (${mib(r.bytes)}).`); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); } } }), el('label', {}, [exNo, ' leave out the takes'])]));
  body.append(el('h4', { textContent: 'Clean up', style: 'padding:0;margin:18px 0 4px' }), el('p', { className: 'note', textContent: 'Removes only what no active line and no tag can reach. The dry run shows how many revisions and how many bytes, and which dropped lines are first copied to .ample/history-archive (Bring back restores from there).' }), el('button', { id: 'cl-go', textContent: 'Clean up...', onclick: async () => { if (await dangerousAct('/api/acts/cleanup', {})) { toast('History cleaned up.'); await loadHistory(); } } }));
}

export function initProjects() {
  $('#btn-projects').onclick = () => (PM.open ? requestClose() : openProjects('recent'));
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape' && PM.open && !document.querySelector('dialog[open]')) { event.preventDefault(); requestClose(); } });   // preventDefault: the dialog it may open must not be closed by this same Escape
}
