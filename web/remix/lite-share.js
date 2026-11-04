// Sharing your own song on the public Remix: the form (title, author, description, how long, the rights), the upload with its progress, the result (the link to share and the management code),
// a .zip to keep a copy, and the little (i) buttons with their short texts. The page (lite.js) gives this module what it needs; nothing here touches the stores of the page.

import { buildForm, checkBuild, neededFiles, pickEntry, sendSummary, sizeText, withHashes } from './lite-build.mjs';
import { zipOfSnapshot } from './lite-zip.mjs';
import { postWithProgress } from './lite-upload.mjs';
import { storeName } from './lite-kv.mjs';

const myShares_ = () => `${storeName()}-shares`;
const author_ = () => `${storeName()}-author`;

/** Copies a text to the clipboard: the modern way, and for a page that may not use it (an old browser, no secure context) the old one. Resolves to true or false. */
export async function copyText(text, field) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    try {
      field.focus();
      field.select();
      return document.execCommand('copy');
    } catch { return false; }
  }
}

/** Wires a Copy to clipboard button to a field and shows the answer: a green tick on the button and a line under it (red, with what to do, when it did not work). */
export function wireCopy(button, field, line) {
  let timer = 0;
  const label = button.textContent;
  button.onclick = async () => {
    const ok = await copyText(field.value, field);
    clearTimeout(timer);
    line.dataset.kind = ok ? 'ok' : 'error';
    line.textContent = ok ? '✓ Copied to the clipboard.' : '✕ Copying did not work here: the text is selected, press Ctrl+C (or hold it on a phone and choose Copy).';
    button.dataset.state = ok ? 'copied' : '';
    button.textContent = ok ? '✓ Copied' : label;
    if (!ok) { field.focus(); field.select(); }
    timer = setTimeout(() => { button.dataset.state = ''; button.textContent = label; }, 2500);
  };
}

/** The (i) buttons: the short text shows on hover, on focus and when the button is clicked or tapped (a second click closes it); Escape and a click elsewhere close it too. */
export function initInfo(root = document) {
  const wraps = [...root.querySelectorAll('.lite-info-wrap')];
  const close = (except = null) => { for (const wrap of wraps) if (wrap !== except) { delete wrap.dataset.open; delete wrap.dataset.pinned; wrap.querySelector('.lite-info').setAttribute('aria-expanded', 'false'); } };
  for (const wrap of wraps) {
    const button = wrap.querySelector('.lite-info');
    const place = () => {
      // fixed to the screen, so the window around it neither clips it nor gets a scroll bar: above the button when it fits, else below, and never outside the screen
      const pop = wrap.querySelector('.lite-pop');
      const margin = 8;
      const width = Math.min(340, window.innerWidth - 2 * margin);
      pop.style.width = `${width}px`;
      const rect = button.getBoundingClientRect();
      const height = pop.offsetHeight;
      pop.style.left = `${Math.max(margin, Math.min(rect.left + rect.width / 2 - width / 2, window.innerWidth - width - margin))}px`;
      pop.style.top = `${rect.top - height - 6 >= margin ? rect.top - height - 6 : Math.min(rect.bottom + 6, window.innerHeight - height - margin)}px`;
    };
    const show = (pinned) => { close(wrap); wrap.dataset.open = '1'; if (pinned) wrap.dataset.pinned = '1'; button.setAttribute('aria-expanded', 'true'); place(); };
    button.addEventListener('mouseenter', () => show(false));
    button.addEventListener('mouseleave', () => { if (!wrap.dataset.pinned) close(); });
    button.addEventListener('focus', () => show(false));
    button.addEventListener('blur', () => { if (!wrap.dataset.pinned) close(); });
    button.addEventListener('click', (event) => { event.preventDefault(); event.stopPropagation(); if (wrap.dataset.pinned) close(); else show(true); });
  }
  document.addEventListener('click', () => close());
  window.addEventListener('resize', () => close());
  let escapedAt = 0;
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape' && wraps.some((w) => w.dataset.open)) { close(); escapedAt = Date.now(); event.preventDefault(); event.stopPropagation(); } }, true);
  // Escape closes the text only, not the form around it (a dialog also closes itself on Escape: that is stopped here)
  for (const dialog of new Set(wraps.map((w) => w.closest('dialog')).filter(Boolean))) dialog.addEventListener('cancel', (event) => { if (Date.now() - escapedAt < 400) event.preventDefault(); });
}

const slug = (text) => (text || 'song').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40) || 'song';

/** Downloads the whole project as a .zip. */
export async function downloadZip(snapshot, name) {
  const zip = await zipOfSnapshot(snapshot);
  const url = URL.createObjectURL(new Blob([zip], { type: 'application/zip' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = `${slug(name)}.zip`;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

/** The shares that this browser made (id, code, title, dates), newest first. The code is what lets the maker change or take down a share, so it is kept here as well. */
export function myShares() {
  try { return JSON.parse(localStorage.getItem(myShares_()) ?? '[]'); } catch { return []; }
}

const remember = (entry) => { try { localStorage.setItem(myShares_(), JSON.stringify([entry, ...myShares()].slice(0, 50))); } catch { /* the browser does not keep it: the code on the screen is the copy */ } };

/**
 * Opens the form for sharing the open project.
 * @param {object} context
 * @param {() => Promise<Array<{path: string, text?: string, blob?: Blob}>>} context.snapshot all the files of the project
 * @param {{id: string, name: string}} context.project
 * @param {() => string | null} context.activeSong the song that is open (or null)
 * @param {object} context.limits the limits of the page
 * @param {() => string} context.token the sign-in token (empty on the public Remix)
 * @param {(text: string, kind?: string) => void} context.toast
 */
export function openPublish({ snapshot, project, activeSong, limits, token, toast }) {
  const box = document.querySelector('#lite-publish-dialog');
  const $ = (selector) => box.querySelector(selector);
  const form = $('#lite-publish-form'), done = $('#lite-publish-done');
  const name = $('#lite-publish-name'), author = $('#lite-publish-author'), desc = $('#lite-publish-desc'), rights = $('#lite-publish-rights');
  const error = $('#lite-publish-error'), progress = $('#lite-publish-progress'), go = $('#lite-publish-go'), summary = $('#lite-publish-files');
  const rightsRow = $('#lite-publish-rights-row');
  form.hidden = false; done.hidden = true;
  error.hidden = true; progress.hidden = true; go.disabled = false;
  name.value = project.name ?? '';
  try { author.value = localStorage.getItem(author_()) ?? ''; } catch { author.value = ''; }
  desc.value = '';
  rights.checked = false;
  // the Share button looks switched off until the box is ticked (it stays clickable: a click shows what is missing); a field that was marked loses its mark when it is touched
  const unmark = () => { for (const field of [name, author]) field.removeAttribute('aria-invalid'); delete rightsRow.dataset.need; };
  const looks = () => { go.setAttribute('aria-disabled', rights.checked ? 'false' : 'true'); };
  looks();
  rights.onchange = () => { looks(); if (rights.checked) delete rightsRow.dataset.need; };
  name.oninput = () => name.removeAttribute('aria-invalid');
  author.oninput = () => author.removeAttribute('aria-invalid');
  box.querySelector('input[name="lite-expires"][value="1w"]').checked = true;
  let files = [];
  let all = [];
  const refresh = async () => {
    all = await snapshot();
    files = await withHashes(neededFiles(all));
    const bytes = files.reduce((sum, f) => sum + (f.blob ? f.blob.size : new TextEncoder().encode(f.text).length), 0);
    summary.textContent = `${files.length} file${files.length === 1 ? '' : 's'} (${sizeText(bytes)}). Files of a built-in demo are not uploaded: the server already has them.`;
  };
  refresh().catch((e) => { summary.textContent = String(e?.message ?? e); });

  const fail = (text) => { error.textContent = text; error.hidden = false; progress.hidden = true; go.disabled = false; };
  $('#lite-publish-cancel').onclick = () => box.close();
  $('#lite-publish-zip').onclick = async () => { try { await downloadZip(await snapshot(), name.value || project.name); } catch (e) { fail(String(e?.message ?? e)); } };

  form.onsubmit = async (event) => {
    event.preventDefault();
    error.hidden = true;
    unmark();
    const missing = [];
    if (!name.value.trim()) { name.setAttribute('aria-invalid', 'true'); missing.push(name); }
    if (!author.value.trim()) { author.setAttribute('aria-invalid', 'true'); missing.push(author); }
    if (!rights.checked) rightsRow.dataset.need = '1';
    if (missing.length || !rights.checked) {
      (missing[0] ?? rights).focus();
      return fail(missing.length ? 'A share needs a title and an author.' : 'Tick the box: you made this, or you may share it.');
    }
    const entry = pickEntry(files.map((f) => f.path), activeSong(), null);
    const problems = checkBuild(entry ?? '', files, limits);
    if (!entry || problems.length) return fail(problems[0] ?? 'Add a song (.amplet) first.');
    go.disabled = true;
    try { localStorage.setItem(author_(), author.value.trim()); } catch { /* not kept */ }
    const expires = box.querySelector('input[name="lite-expires"]:checked').value;
    try {
      const headers = { 'X-Ample-Token': token() };
      const asked = await fetch('/api/remix/blobs/missing', { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' }, body: JSON.stringify({ hashes: [...new Set(files.map((f) => f.sha256))] }) });
      const missing = asked.ok ? new Set((await asked.json()).missing ?? []) : null;
      const send = (needed) => {
        const body = buildForm(entry, files, needed);
        body.set('manifest', JSON.stringify({ title: name.value.trim(), author: author.value.trim(), description: desc.value.trim(), expires, rights: true, entry, files: files.map((f) => ({ path: f.path, sha256: f.sha256 })) }));
        const plan = sendSummary(files, needed);
        summary.textContent = `Sending ${plan.sending} of ${plan.total} files (${sizeText(plan.bytes)}); ${plan.cached} already on the server.`;
        progress.hidden = plan.bytes === 0; progress.value = 0;
        return postWithProgress('/api/remix/shares', { headers, body, onProgress: (sent, total) => { if (total) progress.value = sent / total; } });
      };
      let response = await send(missing);
      if (response.status === 409 && missing) response = await send(null);
      const answer = await response.json().catch(() => ({}));
      if (!response.ok) return fail(answer.error?.message ?? `The share was refused (${response.status}).`);
      const origin = location.origin;
      const link = `${origin}/mix/${answer.id}`;
      const manage = `${link}#manage=${answer.code}`;
      remember({ id: answer.id, code: answer.code, title: name.value.trim(), created: new Date().toISOString(), expires: answer.expires });
      form.hidden = true; done.hidden = false;
      $('#lite-publish-link').value = link;
      $('#lite-publish-manage').value = manage;
      $('#lite-publish-code').textContent = answer.code;
      $('#lite-publish-link-result').textContent = '';
      $('#lite-publish-manage-result').textContent = '';
      $('#lite-publish-zip2').onclick = $('#lite-publish-zip').onclick;
      copyText(link, $('#lite-publish-link')).then((ok) => { const line = $('#lite-publish-link-result'); line.dataset.kind = ok ? 'ok' : 'error'; line.textContent = ok ? '✓ The link is on your clipboard.' : '✕ Copy the link with the button.'; });
    } catch (e) {
      fail(String(e?.message ?? e));
    }
  };
  wireCopy($('#lite-publish-copy-link'), $('#lite-publish-link'), $('#lite-publish-link-result'));
  wireCopy($('#lite-publish-copy-manage'), $('#lite-publish-manage'), $('#lite-publish-manage-result'));
  $('#lite-publish-close').onclick = () => box.close();
  if (!box.open) box.showModal();
  name.focus();
}

/**
 * The management window of the maker: what the share is, a new duration (counted from now) and delete (after a question). The code comes from the link (#manage=CODE) and only goes to the server
 * together with an action, never in an address that the server sees.
 */
export async function openManage({ id, code, token }) {
  const box = document.querySelector('#lite-manage-dialog');
  const $ = (selector) => box.querySelector(selector);
  const message = $('#lite-manage-message'), body = $('#lite-manage-body');
  let current = null;
  const say = (text, kind = '') => { message.textContent = text; message.dataset.kind = kind; };
  const date = (value) => new Date(value).toLocaleString();
  const call = async (action, extra = {}) => {
    const response = await fetch(`/api/remix/shares/${id}/manage`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': token() }, body: JSON.stringify({ code, action, ...extra }) });
    const answer = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(answer.error?.message ?? `Refused (${response.status}).`);
    return answer;
  };
  const show = (share) => {
    current = share;
    $('#lite-manage-title').textContent = share.title;
    $('#lite-manage-info').textContent = `By ${share.author}. Shared ${date(share.created)}. ${share.expires ? `It expires ${date(share.expires)} at the latest.` : 'No end date.'}`;
  };
  body.hidden = true;
  $('#lite-manage-confirm').hidden = true;
  $('#lite-manage-linkbox').hidden = true;
  $('#lite-manage-change').disabled = true;
  for (const radio of box.querySelectorAll('input[name="lite-manage-expires"]')) radio.checked = false;
  say('Looking at your share...');
  if (!box.open) box.showModal();
  $('#lite-manage-close').onclick = () => box.close();
  try {
    const share = await call('info');
    show(share);
    body.hidden = false;
    say('');
    const link = `${location.origin}/mix/${id}`;
    $('#lite-manage-link').value = link;
    wireCopy($('#lite-manage-copy'), $('#lite-manage-link'), $('#lite-manage-copy-result'));
    $('#lite-manage-showlink').onclick = () => { $('#lite-manage-linkbox').hidden = false; };
    for (const radio of box.querySelectorAll('input[name="lite-manage-expires"]')) radio.onchange = () => { $('#lite-manage-change').disabled = false; };
    $('#lite-manage-change').onclick = async () => {
      try {
        const choice = box.querySelector('input[name="lite-manage-expires"]:checked')?.value;
        const changed = await call('expires', { expires: choice });
        show(changed);
        say(changed.expires ? `\u2713 Done: it now expires ${date(changed.expires)} at the latest.` : '\u2713 Done: it has no end date now.', 'ok');
      } catch (error) { say(String(error?.message ?? error), 'error'); }
    };
    $('#lite-manage-zip').onclick = async () => {
      // the share hands out all its files as one .zip: that is the copy to keep
      try {
        say('Preparing the .zip...');
        const response = await fetch(`/api/remix/shares/${id}/archive`, { headers: { 'X-Ample-Token': token() } });
        if (!response.ok) throw new Error(`The .zip could not be made (${response.status}).`);
        const url = URL.createObjectURL(await response.blob());
        const link = document.createElement('a');
        link.href = url;
        link.download = `${slug(current.title)}.zip`;
        document.body.append(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
        say('\u2713 The .zip is downloaded.', 'ok');
      } catch (error) { say(String(error?.message ?? error), 'error'); }
    };
    $('#lite-manage-delete').onclick = () => { $('#lite-manage-confirm').hidden = false; };
    $('#lite-manage-delete-no').onclick = () => { $('#lite-manage-confirm').hidden = true; };
    $('#lite-manage-delete-yes').onclick = async () => {
      try {
        await call('delete');
        body.hidden = true;
        say('\u2713 The share is deleted: nobody can open it any more.', 'ok');
      } catch (error) { say(String(error?.message ?? error), 'error'); }
    };
  } catch (error) {
    say(String(error?.message ?? error), 'error');
  }
}
