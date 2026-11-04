// The window that a shared link (#demo=id) opens: the steps from "opening the demo" to "ready to play" one by one, with a progress bar and the last line of the build log,
// a Cancel while it works, a Try again when it fails (with the "try again in n s" wait of the server), and at the end a big Play button: a click is what a browser needs to start sound.
// The page does the work (lite.js); this file only shows it. The elements are in index.html (#lite-tour).

import { reportLink } from './lite-contact.mjs';

const STEPS = ['open', 'send', 'build', 'ready'];

/**
 * @param {object} options
 * @param {HTMLDialogElement} options.root the dialog
 * @param {() => void} options.onCancel Cancel was pressed (or Escape): stop what is running
 * @param {() => void} options.onPlay Play was pressed
 * @param {() => void} options.onRetry Try again was pressed
 * @param {() => void} [options.onSkip] Skip building was pressed (the button only shows when this is given): open the song, do not build it
 * @param {() => void} [options.onClose] the window went away
 * @param {(button: HTMLElement, seconds: number) => void} [options.wait] shows "Try again in n s" on a button
 */
export function createTour({ root, onCancel, onPlay, onRetry, onSkip = null, onClose = () => {}, wait = null }) {
  const $ = (selector) => root.querySelector(selector);
  const items = Object.fromEntries(STEPS.map((name) => [name, $(`[data-step="${name}"]`)]));
  const buttons = { skip: $('#lite-tour-skip'), cancel: $('#lite-tour-cancel'), play: $('#lite-tour-play'), retry: $('#lite-tour-retry'), close: $('#lite-tour-close') };
  const bar = $('#lite-tour-progress');
  const line = $('#lite-tour-line');
  const files = $('#lite-tour-files');
  const about = $('#lite-tour-about');
  let current = null;
  let uploading = false;
  let cancelled = false;
  let skipped = false;
  let finished = false;

  const show = (...names) => { for (const [name, button] of Object.entries(buttons)) button.hidden = !names.includes(name); };
  const mark = (name, state) => { items[name].dataset.state = state; };

  const api = {
    /** Opens the window at the first step. */
    start(title) {
      finished = false;
      cancelled = false;
      skipped = false;
      for (const name of STEPS) mark(name, 'pending');
      $('#lite-tour-title').textContent = title;
      line.textContent = '';
      line.dataset.kind = '';
      bar.hidden = true; bar.value = 0;
      files.replaceChildren();
      about.hidden = true;
      uploading = false;
      show(...(onSkip ? ['skip', 'cancel'] : ['cancel']));
      if (!root.open) root.showModal();
      api.step('open');
    },
    title(text) { $('#lite-tour-title').textContent = text; },
    /** Who made the song that a shared link opens (plain text only): a third party, the filled-in info, the dates, and the way to complain. */
    about(info) {
      const date = (value) => new Date(value).toLocaleString();
      $('#lite-about-by').textContent = `By ${info.author}`;
      $('#lite-about-desc').textContent = info.description ?? '';
      $('#lite-about-dates').textContent = `Shared ${date(info.created)}. ${info.expires ? `It expires ${date(info.expires)} at the latest (it may disappear earlier).` : 'No end date was chosen (it may still disappear earlier).'}`;
      $('#lite-about-id').textContent = `ID ${info.id}`;
      const reportHref = reportLink(info.id, location.origin);
      if (reportHref) $('#lite-about-report').href = reportHref;
      // a permanent share is one of AMPLE's own songs (the docs): no third-party banner, no dates, no complaint line
      const own = info.permanent === true;
      $('#lite-about-dates').hidden = own;
      $('#lite-about-report').closest('p').hidden = own || !reportHref;
      const banner = about.querySelector('.lite-about-banner');
      if (banner) banner.hidden = own;
      about.hidden = false;
    },
    /** This step is running; the ones before it are done. */
    step(name) {
      if (finished) return;
      current = name;
      let before = true;
      for (const step of STEPS) {
        if (step === name) { mark(step, 'active'); before = false; } else mark(step, before ? 'done' : 'pending');
      }
      bar.hidden = !(name === 'build' || (name === 'send' && uploading));
      if (name === 'build') bar.value = 0;
    },
    /** The plan of the files: how many are cached on the server, how many are sent and how big that is. */
    sending(plan, size) {
      const pill = (kind, text) => { const span = document.createElement('span'); span.className = `lite-pill ${kind}`; span.textContent = text; return span; };
      files.replaceChildren(pill('cached', `${plan.cached} cached on the server`), pill('sent', plan.sending > 0 ? `${plan.sending} to send (${size})` : 'nothing to send'));
      uploading = plan.bytes > 0;
      if (uploading && current === 'send') { bar.hidden = false; bar.value = 0; }
    },
    /** How far the sending is. */
    uploaded(sent, total, sentText, totalText) {
      if (finished || current !== 'send') return;
      bar.hidden = false;
      bar.value = sent / total;
      line.textContent = `Sending... ${Math.round((100 * sent) / total)}% (${sentText} of ${totalText})`;
    },
    line(text) { if (!finished) { line.textContent = text; line.dataset.kind = ''; } },
    progress(fraction) { if (fraction >= 0) bar.value = fraction; },
    /** Everything is built: the Play button is the way on. */
    ready() {
      finished = true;
      for (const step of STEPS) mark(step, 'done');
      bar.hidden = true;
      line.textContent = 'Ready. Press play.';
      line.dataset.kind = '';
      show('play', 'close');
      buttons.play.focus();
    },
    /** Something stopped it: the step that was running gets a cross, the line says why. */
    fail(message, retryAfter = 0) {
      finished = true;
      if (current) mark(current, 'error');
      bar.hidden = true;
      line.textContent = message;
      line.dataset.kind = 'error';
      show('retry', 'close');
      if (!root.open) root.showModal();   // the window was closed by Skip: a problem is still shown
      buttons.retry.disabled = false;
      buttons.retry.textContent = 'Try again';
      if (retryAfter > 0 && wait) wait(buttons.retry, retryAfter);
    },
    close() { if (root.open) root.close(); },
    /** Skip building was pressed since the last start. */
    wasSkipped() { return skipped; },
    /** Cancel was pressed since the last start. */
    isCancelled() { return cancelled; },
  };

  buttons.cancel.onclick = () => { cancelled = true; onCancel(); };
  buttons.skip.onclick = () => { skipped = true; onSkip?.(); api.close(); };
  buttons.play.onclick = () => { onPlay(); api.close(); };
  buttons.retry.onclick = () => onRetry();
  buttons.close.onclick = () => api.close();
  // properties, not addEventListener: a window that is used again must not collect a handler for every tour
  root.oncancel = (event) => { if (!finished) { event.preventDefault(); cancelled = true; onCancel(); } };   // Escape while it works is Cancel, not a silent close
  root.onclose = () => onClose();
  return api;
}
