// The first-time setup wizard (plan/first-run-wizard.md): the project settings as pages, walked through once per project before the studio goes on
// (and, for a new project, before the demo song builds and plays by itself). The position in the wizard lives only in this page: F5 starts again at the first
// step, with what was saved on the earlier steps filled in. @ample:first-run @ample:project-settings

import { api } from './api.js';
import { el } from './ui.js';
import { settingsForWizard } from './projects.js';

/** The pages. `since` is the wizard version in which a page appeared: a later wizard asks again with only the pages newer than what a project has done. */
const PAGES = [
  { id: 'welcome', title: 'Welcome', since: 1 },
  { id: 'general', title: 'General', since: 1, tab: 'general', intro: 'How much history AMPLE keeps, which engine is the default, how many blocks a build renders at once, and the language of Koda, the helper.' },
  { id: 'audio', title: 'Audio routing', since: 1, tab: 'audio', intro: 'Where songs, voices and stems are made: on this machine, or on a remote GPU (Faer). Local is fine to start with.' },
  { id: 'search', title: 'Web search', since: 1, tab: 'search', intro: 'Optional. The assistant can search the web; a Brave Search key makes that better. Without a key it uses DuckDuckGo.' },
  { id: 'saves', title: 'Safe saves', since: 1, tab: 'saves', intro: 'Checkpoints around every save, and an autosave that runs on the server.' },
  { id: 'assistant', title: 'Assistant', since: 1, tab: 'assistant', intro: 'Optional. The Codex assistant proposes changes; nothing changes until you accept a proposal.' },
  { id: 'seed', title: 'Seed and folders', since: 1, tab: 'seed', intro: 'The master seed that blocks without a seed of their own derive theirs from, and the folders linked to this project. You can leave both as they are.' },
  { id: 'ready', title: 'Ready', since: 1 },
];

/** The state of the wizard for the open project, or null when it cannot be read (then the studio just goes on). */
export async function firstRunState() {
  try { return await api.get('/api/project/first-run'); } catch { return null; }
}

/**
 * Shows the wizard over the studio and resolves when it was finished: { open, play } says what the studio opens (the demo song of a new project, which then plays).
 * Nothing behind it can be used: no way out, and the studio below it is inert.
 */
export function runFirstRunWizard(state, projectName = '') {
  return new Promise((resolve) => {
    const pages = PAGES.filter((page) => page.since > (state.doneVersion || 0));
    const isNew = state.origin === 'new';
    const host = el('div', { id: 'fr', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'fr-title' });
    const holder = el('div', { hidden: true });
    let controls = null, at = 0, busy = false;

    const steps = el('ol', { className: 'fr-steps' }, pages.map((page, i) => el('li', { id: `fr-step-${page.id}`, textContent: page.title, 'data-index': String(i) })));
    const title = el('h2', { id: 'fr-title' });
    const body = el('div', { className: 'fr-body', id: 'fr-body' });
    const back = el('button', { id: 'fr-back', type: 'button', textContent: 'Back', onclick: () => go(at - 1) });
    const next = el('button', { id: 'fr-next', type: 'button', className: 'primary', textContent: 'Next', onclick: () => advance() });
    const message = el('p', { id: 'fr-message', className: 'note', role: 'status' });
    host.append(el('aside', { className: 'fr-side' }, [el('h3', { textContent: 'First-time setup' }), el('p', { className: 'note', textContent: projectName }), steps]), el('main', { className: 'fr-main' }, [title, body, message, el('div', { className: 'fr-foot' }, [back, el('span', { className: 'sp' }), next])]));

    // nothing behind the wizard can be used while it is open (dialogs and the toast stay usable: a failed save says why)
    const behind = [...document.body.children].filter((node) => node.tagName !== 'SCRIPT' && node.tagName !== 'DIALOG' && node.id !== 'toast');
    behind.forEach((node) => { node.inert = true; });
    document.body.append(host);
    document.body.classList.add('fr-open');

    const text = (lines) => lines.map((line) => el('p', { textContent: line }));
    const page = () => pages[at];

    function paint() {
      const current = page();
      title.textContent = current.title;
      body.replaceChildren();
      message.textContent = '';
      steps.querySelectorAll('li').forEach((li, i) => { li.classList.toggle('on', i === at); li.classList.toggle('done', i < at); li.setAttribute('aria-current', i === at ? 'step' : 'false'); });
      if (current.id === 'welcome') {
        body.append(...text(isNew
          ? ['Welcome to AMPLE. This is a new project: a few pages with its settings first, then the demo song "Welcome to AMPLE" opens, builds and plays by itself.', 'Every page has sensible defaults. If you do not care about a setting, press Next.']
          : ['This project was made before the first-time setup existed. This is a one-time look at its settings: what is set now is shown on every page, and nothing changes unless you change it.', 'Press Next on every page to go through. You find all of this again later under Projects, Project settings.']));
      } else if (current.tab) {
        body.append(el('p', { className: 'note', textContent: current.intro }));
        const panel = controls.panel(current.tab);
        panel.hidden = false;
        body.append(panel);
      } else {
        body.append(...text([isNew ? 'That is everything. Finish opens the demo song, builds it and plays it.' : 'That is everything. Finish takes you back to your song.', 'You can change any of these settings later under Projects, Project settings. "ample doctor --env" tells you what else AMPLE can use on this computer (ffmpeg for MP3, for example).']));
      }
      back.disabled = at === 0;
      const last = at === pages.length - 1;
      next.textContent = last ? 'Finish' : 'Next';
      next.id = 'fr-next';
      const focusTarget = body.querySelector('input, select, button') || next;
      focusTarget.focus({ preventScroll: true });
    }

    function go(index) {
      if (busy || index < 0 || index >= pages.length) return;
      at = index;
      paint();
    }

    async function advance() {
      if (busy || !controls) return;
      busy = true; next.disabled = back.disabled = true; message.textContent = '';
      try {
        const current = page();
        if (current.tab && !(await controls.save(current.tab))) { message.textContent = 'This page could not be saved: see the message, correct it and press Next again.'; return; }
        if (at < pages.length - 1) { busy = false; go(at + 1); return; }
        // Finish: nothing may be left unsaved on a page that was visited and then left with Back
        for (const p of pages) if (p.tab && !(await controls.save(p.tab))) { message.textContent = `The page "${p.title}" could not be saved.`; at = pages.indexOf(p); busy = false; paint(); return; }
        const result = await api.post('/api/project/first-run/complete', {});
        host.remove(); behind.forEach((node) => { node.inert = false; }); document.body.classList.remove('fr-open');
        resolve({ open: result.open || null, play: !!result.play });
      } catch (error) {
        message.textContent = `${error.code ? `${error.code}: ` : ''}${error.message || error}`;
      } finally {
        busy = false; next.disabled = false; back.disabled = at === 0;
      }
    }

    host.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') event.preventDefault();   // there is no way out before Finish
      else if (event.key === 'Enter' && !event.shiftKey && !(event.target instanceof HTMLTextAreaElement) && !(event.target instanceof HTMLButtonElement) && !(event.target instanceof HTMLSelectElement)) { event.preventDefault(); advance(); }
    });

    // loading, ready and error states: forward navigation stays disabled until the settings controls exist; on an error Retry or "Later" (the setup stays pending)
    function load() {
      controls = null; next.disabled = back.disabled = true; body.replaceChildren(el('p', { className: 'note', textContent: 'Loading the project settings...' }));
      settingsForWizard(holder).then((c) => { controls = c; next.disabled = false; paint(); }).catch((error) => {
        body.replaceChildren(el('p', { className: 'seerr error', textContent: `${error.code ? `${error.code}: ` : ''}${error.message || error}` }),
          el('button', { id: 'fr-retry', type: 'button', className: 'primary', textContent: 'Retry', onclick: () => load() }),
          el('button', { id: 'fr-later', type: 'button', textContent: 'Later (open the project as it is)', onclick: () => { host.remove(); behind.forEach((node) => { node.inert = false; }); document.body.classList.remove('fr-open'); resolve({ open: null, play: false }); } }));
      });
    }
    load();
  });
}
