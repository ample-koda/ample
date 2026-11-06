// The optional AI assistant (Codex). Codex proposes, AMPLE validates, the user decides: this file has the settings of the assistant, the places where the user can ask for help,
// the prompt, and the proposal wizard (what Codex says, the diff, the checks, listening to the result, accept / accept anyway / reject).
// Nothing here changes the project by itself: the project only changes when the user accepts a proposal.
// @ample:ai

import { api } from './api.js';
import { humAttachment, offerAttachment, previewAttachment } from './hum.js';
import { S, applyAiBuild, applyText, enterAiPeek, notify, reloadDoc, subscribe } from './store.js';
import { onPlayer, playerState, stop } from './player.js';
import { ask, el, toast } from './ui.js';
import { registerPlaybackAudio } from './playback-volume.js';

/** The "Ai Assistant" lockup (icon tile + text), same neon-tube recipe as the AMPLE wordmark; used in the Assistant panel header. */
const AI_ASSISTANT_LOGO_SVG = `<svg width="123" height="37" viewBox="0 0 240 72" xmlns="http://www.w3.org/2000/svg" role="img" aria-hidden="true" focusable="false">
  <rect x="3" y="3" width="66" height="66" rx="17" fill="#11161b" stroke="#36404a" stroke-width="1.6"/>
  <g transform="translate(14 19)">
    <g fill="none" stroke="url(#ai-logo-g)" stroke-width="5.6" stroke-linecap="round">
      <path d="M4 36 L12.5 7 L21 36"/>
      <path d="M7.5 23.5 H17.5"/>
      <path d="M30.5 18 V36"/>
    </g>
    <g fill="#19e3c8">
      <ellipse cx="34.8" cy="9.7" rx="3.9" ry="3" transform="rotate(-18 34.8 9.7)"/>
      <path d="M38.8 8.3 V-1.6 q5.1 1.7 7.3 7.3 a1.9 1.9 0 0 1 -3.1 2 q-1.7 -2.3 -4.2 -2.9 Z"/>
    </g>
  </g>
  <defs><linearGradient id="ai-logo-g" x1="4" y1="4" x2="68" y2="68" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#2de2ff"/><stop offset="1" stop-color="#ff4fd8"/></linearGradient></defs>
  <text x="86" y="44" font-family="system-ui, sans-serif" font-weight="600" font-size="27" fill="currentColor">Assistant</text>
</svg>`;

let status = null;
const say = (error) => toast(`${error.code ? `${error.code}: ` : ''}${error.message || error}`);
// readable names for the reasoning efforts (the values stay what the server expects)
const EFFORT_LABELS = { '': 'server setting', default: 'let the model decide', low: 'Low', medium: 'Medium', high: 'High', xhigh: 'Extra high' };
const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

/** Where the assistant stands (cached for a moment, so menus can be built without waiting). */
export async function aiStatus(force = false, auth = false) {
  if (force || auth || !status) status = await api.get(`/api/ai/status${auth ? '?auth=true' : ''}`);
  return status;
}

const configured = () => !!status && status.project.assistant !== 'none';

function badge(st = status) {
  if (!st || st.project.assistant === 'none') return el('span', { className: 'assistant-logo', innerHTML: AI_ASSISTANT_LOGO_SVG, 'aria-label': 'Assistant' });
  return el('span', { className: `ai-badge ${st.project.mode}${st.available ? '' : ' off'}`, title: st.available ? `${st.identity}, execution mode ${st.modeLabel}` : st.problems.join(' ') }, [
    el('b', { textContent: 'Codex' }), el('span', { textContent: st.project.assistant === 'codex-device' ? 'Project Device ID' : 'Server account' }), el('span', { className: 'mode', textContent: st.modeLabel }),
  ]);
}

function dialogBox(title, id) {
  document.getElementById(id)?.close();
  document.getElementById(id)?.remove();
  const close = el('button', { type: 'button', className: 'tx-x', textContent: '✕', 'aria-label': 'Close (Esc)', title: 'Close (Esc)', 'aria-label': 'Close' });
  const stopMusic = el('button', { type: 'button', className: 'audio-stop playing ai-modal-stop', textContent: '■', 'aria-label': 'Stop music', title: 'Stop music', 'aria-label': 'Stop music', onclick: () => { stopPreview(); stop(); updateStop(); } });
  const updateStop = () => { stopMusic.hidden = !playerState().playing && !S.aiPlaying; };
  const body = el('div', { className: 'tx-body' });
  const foot = el('div', { className: 'tx-foot' });
  const head = el('div', { className: 'tx-head' }, [el('h2', { textContent: title }), el('span', { className: 'sp' }), stopMusic, close]);
  const box = el('dialog', { id, className: 'tx-dialog ai-dialog', 'aria-label': title }, [head, body, foot]);
  close.onclick = () => box.close();
  box.addEventListener('close', () => box.remove());
  const offPlayer = onPlayer(updateStop), offStore = subscribe(updateStop);
  box.addEventListener('close', () => { offPlayer(); offStore(); });
  updateStop();
  document.body.append(box);
  box.showModal();
  return { box, body, foot, head };
}

const section = (title, ...kids) => el('div', { className: 'tx-sec' }, [el('h3', { textContent: title }), ...kids]);

/** Inspect the original operation payload; old sessions can have only a compact summary. */
function showToolDetails(entry) {
  const { box, body, foot } = dialogBox('Tool call details', 'ai-tool-details');
  box.classList.add('ai-tool-details');
  const pretty = (value) => el('pre', { className: 'ai-tool-json', textContent: value === undefined ? 'Not captured for this call.' : typeof value === 'string' ? value : JSON.stringify(value, null, 2) });
  const details = entry.details;
  body.append(section('Operation', el('p', { textContent: entry.text }), el('small', { textContent: entry.at || '' })));
  if (!details) {
    body.append(el('p', { className: 'note', textContent: 'This activity entry has no original payload. Older sessions retain only the summary; full input and output are captured by the updated server.' }));
  } else {
    body.append(section('Metadata', pretty(Object.fromEntries(Object.entries(details).filter(([key]) => !['arguments', 'result', 'error', 'aggregatedOutput'].includes(key))))));
    body.append(section('Input', pretty(details.arguments ?? (details.command ? { command: details.command, cwd: details.cwd } : undefined))));
    body.append(section('Output', pretty(details.result ?? details.aggregatedOutput)));
    if (details.error) body.append(section('Error', pretty(details.error)));
    const calls = details.arguments?.calls;
    if (Array.isArray(calls)) {
      let result = details.result?.structuredContent ?? details.result;
      for (const block of details.result?.structuredContent ? [] : details.result?.content || []) {
        if (block.type !== 'text') continue;
        try { result = JSON.parse(block.text); break; } catch { /* Non-JSON content remains visible in Output. */ }
      }
      const textResults = (details.result?.content || []).filter((block) => block.type === 'text').flatMap((block) => block.text.split(/^### \d+\. .*\r?\n/gm).slice(1));
      const results = Array.isArray(result) ? result : result?.results ?? textResults;
      body.append(section(`Batch calls (${calls.length})`, ...calls.map((call, index) => el('details', { className: 'ai-tool-batch' }, [
        el('summary', { textContent: `${index + 1}. ${call.tool || 'Unknown tool'}` }),
        section('Input', pretty(call.arguments)),
        section('Output', pretty(Array.isArray(results) ? results[index] : undefined)),
      ]))));
    }
    body.append(el('details', { className: 'ai-tool-batch' }, [el('summary', { textContent: 'Original MCP / operation payload' }), pretty(details)]));
  }
  foot.append(el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }));
}
const opt = (type, name, value, label, hint, checked, disabled, onchange) => el('label', { className: `tx-opt${disabled ? ' disabled' : ''}` }, [el('input', { type, name, value, checked, disabled, onchange }), el('span', {}, [el('b', { textContent: label }), el('small', { textContent: hint })])]);

/** Render the Markdown subset used in Codex replies with DOM nodes only; raw HTML is always text. */
function renderAiMarkdown(markdown) {
  const root = el('div', { className: 'ai-markdown' });
  const inline = (text, host) => {
    const pattern = /(`[^`\n]+`|\*\*[^*\n]+\*\*|__[^_\n]+__|\*[^*\n]+\*|_[^_\n]+_|~~[^~\n]+~~|\[[^\]\n]+\]\([^\s)]+\))/g;
    let start = 0, match;
    while ((match = pattern.exec(text))) {
      host.append(document.createTextNode(text.slice(start, match.index)));
      const token = match[0];
      if (token[0] === '`') host.append(el('code', { textContent: token.slice(1, -1) }));
      else if (token.startsWith('**') || token.startsWith('__')) { const node = el('strong'); inline(token.slice(2, -2), node); host.append(node); }
      else if (token.startsWith('~~')) { const node = el('del'); inline(token.slice(2, -2), node); host.append(node); }
      else if (token[0] === '*' || token[0] === '_') { const node = el('em'); inline(token.slice(1, -1), node); host.append(node); }
      else {
        const link = /^\[([^\]]+)\]\(([^\s)]+)\)$/.exec(token);
        let url;
        try { url = new URL(link[2], location.href); } catch {}
        if (url && ['http:', 'https:'].includes(url.protocol)) { const anchor = el('a', { href: url.href, target: '_blank', rel: 'noopener noreferrer' }); inline(link[1], anchor); host.append(anchor); }
        else host.append(document.createTextNode(token));
      }
      start = pattern.lastIndex;
    }
    host.append(document.createTextNode(text.slice(start)));
  };
  const lines = String(markdown || '').replace(/\r\n?/g, '\n').split('\n');
  const isTableRow = (line) => /^\s*\|?.+\|.+\|?\s*$/.test(line);
  const tableCells = (line) => line.trim().replace(/^\||\|$/g, '').split(/(?<!\\)\|/).map((cell) => cell.replace(/\\\|/g, '|').trim());
  const isBlockStart = (line, next) => /^\s*$|^\s*```|^\s{0,3}#{1,6}\s|^\s*>|^\s*[-*+]\s|^\s*\d+[.)]\s|^\s*([-*_]\s*){3,}$/.test(line) || (isTableRow(line) && /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$/.test(next || ''));
  for (let i = 0; i < lines.length;) {
    if (!lines[i].trim()) { i++; continue; }
    const fence = /^\s*```([^\s`]*)\s*$/.exec(lines[i]);
    if (fence) {
      const code = []; i++;
      while (i < lines.length && !/^\s*```\s*$/.test(lines[i])) code.push(lines[i++]);
      if (i < lines.length) i++;
      const pre = el('pre'); pre.append(el('code', { className: fence[1] ? `language-${fence[1].replace(/[^\w+-]/g, '')}` : '', textContent: code.join('\n') })); root.append(pre); continue;
    }
    const heading = /^\s{0,3}(#{1,6})\s+(.*)$/.exec(lines[i]);
    if (heading) { const h = el(`h${heading[1].length}`); inline(heading[2].replace(/\s+#+\s*$/, ''), h); root.append(h); i++; continue; }
    if (/^\s*([-*_]\s*){3,}$/.test(lines[i])) { root.append(el('hr')); i++; continue; }
    if (/^\s*>/.test(lines[i])) {
      const quote = []; while (i < lines.length && /^\s*>/.test(lines[i])) quote.push(lines[i++].replace(/^\s*> ?/, ''));
      const block = renderAiMarkdown(quote.join('\n')); root.append(el('blockquote', {}, [...block.childNodes])); continue;
    }
    if (isTableRow(lines[i]) && i + 1 < lines.length && /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$/.test(lines[i + 1])) {
      const table = el('table'), headRow = el('tr');
      for (const cell of tableCells(lines[i])) { const th = el('th'); inline(cell, th); headRow.append(th); }
      table.append(el('thead', {}, [headRow])); i += 2;
      const rows = el('tbody');
      while (i < lines.length && isTableRow(lines[i]) && lines[i].trim()) { const row = el('tr'); for (const cell of tableCells(lines[i])) { const td = el('td'); inline(cell, td); row.append(td); } rows.append(row); i++; }
      table.append(rows); root.append(table); continue;
    }
    const listMatch = /^\s{0,3}([-*+]|\d+[.)])\s+(.*)$/.exec(lines[i]);
    if (listMatch) {
      const ordered = /^\d/.test(listMatch[1]), list = el(ordered ? 'ol' : 'ul');
      while (i < lines.length) { const item = /^\s{0,3}([-*+]|\d+[.)])\s+(.*)$/.exec(lines[i]); if (!item || /^\d/.test(item[1]) !== ordered) break; const li = el('li'); inline(item[2], li); list.append(li); i++; }
      root.append(list); continue;
    }
    const paragraph = [lines[i++]];
    while (i < lines.length && !isBlockStart(lines[i], lines[i + 1])) paragraph.push(lines[i++]);
    const p = el('p'); inline(paragraph.join('\n'), p); root.append(p);
  }
  return root;
}

// ---- the places where the user can ask for help ----

const SCOPES = [['selection', 'Selection only'], ['block', 'Current block'], ['sequence', 'Current sequence'], ['file', 'Current source file'], ['linked', 'Current source + linked sequences'], ['project', 'Project']];

/** The menu entries of the AI assistant for a place: `source`, `sequence` or `block`. The context says what the entry works on. */
export function aiItems(context) {
  const ask = (title, kind, scope, hint, extra = {}) => ({ label: title, fn: () => openAiPrompt({ title: title.replace(/\.\.\.$/, ''), kind, scope, hint, ...context, ...extra }) });
  const head = status ? { head: status.project.assistant === 'none' ? 'Assistant: off' : `${status.identity} · ${status.modeLabel}` } : { head: 'Assistant' };
  if (!configured()) return [head, { label: 'Set up the Assistant...', fn: () => openAiSettings() }];
  if (!status.available) return [head, { label: status.problems[0] || 'The assistant is not available', fn: () => openAiSettings() }, { label: 'Open the Assistant settings...', fn: () => openAiSettings() }];
  const has = !!context.selection?.();
  if (context.context === 'sequence') return [head, ask('Generate sequence...', 'generate-sequence', 'sequence', 'Create a hesitant walking rhythm that slowly becomes more confident.'), ask('Modify sequence...', 'modify-sequence', 'sequence', 'Make the hi-hat pattern less regular.'), ask('Extend sequence...', 'modify-sequence', 'sequence', 'Extend this sequence by four bars while preserving the original motif.'), ask('Explain sequence...', 'explain', 'sequence', 'Explain how this sequence is built.'), ask('Improve sequence...', 'modify-sequence', 'sequence', 'Improve the groove without changing the length.')];
  if (context.context === 'blocks') return [head, ask(`New Assistant session with ${context.contextLabel || 'this block context'}...`, 'custom', 'block', 'Use these blocks as the starting context. Keep changes focused here when practical; if the requested result needs connected blocks or a wider project change, make that change and explain why.')];
  if (context.context === 'canvas') return [head, ask('New Assistant session (no block focus)...', 'custom', 'project', 'What would you like the assistant to do in this project?')];
  if (context.context === 'block') return [head, ask('Generate/update block comment...', 'comment', 'block', 'Write a short comment above this block that says what it is for.'), ask('Explain this block...', 'explain', 'block', 'Explain what this block does and why it sounds the way it does.'), ask('Suggest documentation...', 'comment', 'block', 'Document the settings of this block as comments.'), ask('Suggest code change...', 'custom', 'block', 'Make this block sound warmer.')];
  return [head, ask('Generate code here...', 'custom', has ? 'selection' : 'file', 'Add a bass block that follows the chords.'), ask('Modify selection...', 'modify-selection', 'selection', 'Make the selected blocks quieter.'), ask('Modify current file...', 'modify-file', 'file', 'Change the tempo of the song to 100 and adjust what depends on it.'), ask('Modify current file + linked sequences...', 'modify-linked', 'linked', 'Make the chorus more energetic in the song and in its sequences.'), ask('Refactor...', 'refactor', 'file', 'Refactor repeated code into something reusable.'), ask('Generate comments...', 'comment', has ? 'selection' : 'file', 'Add comments that explain why these effects are used.'), ask('Explain selection...', 'explain', has ? 'selection' : 'file', 'Explain what this does.')];
}

/** Opens a menu of the assistant at a place on the screen. */
export async function openAiMenu(x, y, context) {
  const { showMenu } = await import('./ui.js');
  await aiStatus(true).catch(() => {});
  showMenu(x, y, aiItems(context));
}

// ---- the prompt ----

export function openAiPrompt(options) {
  const { box, body, foot, head } = dialogBox(options.title || 'Ask Assistant', 'ai-prompt');
  const assistantBadge = badge();
  head.insertBefore(assistantBadge, head.querySelector('.sp'));
  aiStatus(true).then((st) => { if (box.isConnected) assistantBadge.replaceWith(badge(st)); }).catch(() => {});
  const prompt = el('textarea', { id: 'ai-prompt-text', rows: 6, placeholder: options.hint || 'What should the assistant do?', 'aria-label': 'What should the assistant do?' });
  const model = el('select', { id: 'ai-request-model', 'aria-label': 'Model for this request' }, [el('option', { value: '', textContent: 'Use server default' }), el('option', { value: 'default', textContent: 'Let the assistant choose its default' })]);
  const effort = el('select', { id: 'ai-request-effort', 'aria-label': 'Reasoning effort for this request' }, [el('option', { value: '', textContent: 'Use server setting' }), ...['default', 'low', 'medium', 'high', 'xhigh'].map((v) => el('option', { value: v, textContent: v === 'default' ? 'Let the model choose' : v }))]);
  const strict = el('input', { id: 'ai-request-strict', type: 'checkbox' });
  const usage = el('div', { className: 'ai-usage tx-hint', role: 'status', textContent: 'Loading usage...' });
  api.get('/api/ai/models').then((r) => {
    const current = r.default || 'gpt-6-luna';
    for (const m of r.models || []) model.append(el('option', { value: m.id, textContent: `${m.name || m.id}${m.id === current ? ' (server default)' : ''}` }));
    const buckets = r.usage?.rateLimitsByLimitId || (r.usage?.rateLimits ? { codex: r.usage.rateLimits } : {});
    const rows = Object.entries(buckets).flatMap(([id, limit]) => [limit?.primary, limit?.secondary].filter(Boolean).map((window) => {
      const duration = window.windowDurationMins >= 60 && window.windowDurationMins % 60 === 0 ? `${window.windowDurationMins / 60}h` : `${window.windowDurationMins}m`;
      const reset = window.resetsAt ? ` · resets ${new Date(window.resetsAt * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : '';
      return `${limit.limitName || id}: ${Math.max(0, 100 - Math.round(window.usedPercent || 0))}% left${window.windowDurationMins ? ` · ${duration} window` : ''}${reset}`;
    }));
    usage.textContent = rows.length ? rows.join('  |  ') : 'Usage limits are unavailable for this account.';
  }).catch(() => { usage.textContent = 'Usage limits are unavailable.'; });
  const scope = el('select', { id: 'ai-scope', 'aria-label': 'Scope' }, SCOPES.map(([v, label]) => el('option', { value: v, textContent: label, disabled: v === 'selection' && !options.selection?.() })));
  scope.value = options.scope === 'selection' && !options.selection?.() ? 'file' : options.scope;
  const note = el('p', { className: 'tx-hint' });
  const explain = () => { note.textContent = { selection: 'Only the selected lines are sent as the part to change; the assistant still sees the file.', block: 'The block and its surroundings.', sequence: 'The sequence file and the song that uses it.', file: 'The whole source file.', linked: 'The source file and the sequences, lyrics and MIDI text files its blocks use.', project: 'All the text files of the project. Use this for larger changes.' }[scope.value]; };
  const selectedRange = options.selection?.();
  let selectionEditor = null, selectionLoading = false;
  const selectionCode = el('div', { className: 'ai-selection-code' });
  const selectionPreview = options.selectionText ? el('div', { className: 'ai-selection-preview' }, [el('b', { textContent: selectedRange ? `Selected lines ${selectedRange.from}–${selectedRange.to}` : 'Selected source lines' }), selectionCode]) : null;
  const defaultPrompt = (value) => value === 'selection'
    ? options.kind === 'explain' ? 'What does this selected code do in context, and what should I know about it?' : 'Please make a focused improvement to the selected code. Keep everything outside the selection unchanged and explain why the change helps.'
    : value === 'file'
      ? options.kind === 'explain' ? 'What does this file do, and how do its main parts work together?' : 'Please review this whole file and suggest the most useful focused improvement while preserving its intent. Explain why it would help.'
      : '';
  if (!prompt.value) { prompt.value = options.prefill || defaultPrompt(scope.value); if (!options.prefill && prompt.value) prompt.dataset.defaulted = '1'; }
  prompt.addEventListener('input', () => { prompt.dataset.userEdited = '1'; prompt.dataset.prefilled = ''; });
  const showSelectionPreview = async () => {
    if (!selectionPreview) return;
    selectionPreview.hidden = scope.value !== 'selection';
    if (selectionPreview.hidden || selectionEditor || selectionLoading) return;
    selectionLoading = true;
    selectionCode.style.height = `${Math.min(220, Math.max(42, options.selectionText.split('\n').length * 20 + 12))}px`;
    try {
      const { createAmplePreview } = await import('../vendor/codemirror-ample.js');
      if (!box.isConnected) return;
      selectionEditor = createAmplePreview(selectionCode, options.selectionText);
      box.addEventListener('close', selectionEditor.destroy, { once: true });
    } catch {
      selectionCode.textContent = options.selectionText;
    } finally { selectionLoading = false; }
  };
  scope.onchange = () => { explain(); showSelectionPreview(); if (!options.prefill && !prompt.dataset.userEdited && !prompt.dataset.prefilled) { prompt.value = defaultPrompt(scope.value); prompt.dataset.defaulted = prompt.value ? '1' : ''; } };
  explain();
  const hum = humAttachment();
  const offer = offerAttachment({ dropTarget: box });
  // earlier requests of this project: pick one to continue it (its prompt, result and changes go to Codex), or to tweak it
  let continueFrom = options.continueFrom || null;
  const earlierNote = el('p', { className: 'tx-hint', id: 'ai-earlier-note' });
  const earlier = el('select', { id: 'ai-earlier', 'aria-label': 'Continue an earlier request' }, [el('option', { value: '', textContent: 'A new request' })]);
  const showEarlier = (list) => { const e = list.find((x) => x.id === continueFrom); earlierNote.textContent = e ? `This continues your request of ${(e.at || '').slice(0, 10)} (${e.status}): the assistant gets what was asked, what it answered and what it changed.` : ''; };
  let earlierList = [];
  api.get(`/api/ai/history?limit=15&song=${encodeURIComponent(options.song || S.path || '')}`).then((r) => {
    earlierList = r.requests || [];
    for (const e of earlierList) earlier.append(el('option', { value: e.id, textContent: `${(e.at || '').slice(0, 10)} · ${e.status} · ${(e.prompt || '').slice(0, 70)}`, selected: e.id === continueFrom }));
    showEarlier(earlierList);
  }).catch(() => {});
  earlier.onchange = () => {
    continueFrom = earlier.value || null;
    const e = earlierList.find((x) => x.id === continueFrom);
    if (e && (!prompt.value.trim() || prompt.dataset.prefilled === '1' || prompt.dataset.defaulted === '1')) { prompt.value = e.prompt; prompt.dataset.prefilled = '1'; prompt.dataset.defaulted = ''; }
    showEarlier(earlierList);
  };
  const contextSelect = el('select', { id: 'ai-context', 'aria-label': 'What should the assistant see?' }, [el('option', { value: 'unsaved', textContent: 'My unsaved changes too (what is on the screen)' }), el('option', { value: 'saved', textContent: 'Saved files only (what is on disk)' })]);
  const contextRow = el('div', { className: 'hum-row' }, [contextSelect, el('small', { className: 'note', textContent: 'With saved files only, a proposal for a file you have changed since is shown as a conflict when you accept it.' })]);
  const go = el('button', { id: 'ai-ask', type: 'button', className: 'primary', textContent: 'Ask Assistant', onclick: async () => {
    if (!prompt.value.trim()) { prompt.focus(); return; }
    const path = options.path || S.path;
    if (!path) { say(new Error('Open a song before starting an assistant request.')); return; }
    go.disabled = true;
    try {
      const sel = options.selection?.();
      const task = await api.post('/api/ai/tasks', { sessionId: options.sessionId, kind: options.kind, context: contextSelect.value, scope: scope.value, prompt: prompt.value.trim(), path, song: options.song || S.path || path, fromLine: scope.value === 'selection' ? sel?.from : undefined, toLine: scope.value === 'selection' ? sel?.to : undefined, block: options.block, blocks: options.blocks, model: model.value || undefined, effort: effort.value || undefined, strict: strict.checked, hums: hum.ids(), offered: offer.ids(), continueFrom });
      box.close();
      openAiTask(task.id, options);
    } catch (error) { go.disabled = false; say(error); }
  } });
  prompt.addEventListener('keydown', (event) => { if (event.key === 'Enter' && event.ctrlKey) { event.preventDefault(); go.click(); } });
  body.append(
    el('p', { className: 'tx-lead', textContent: `The assistant proposes, AMPLE checks it, you decide: nothing in the project changes until you accept the proposal. ${options.path ? `On ${options.path}${options.contextLabel ? `, starting from ${options.contextLabel}` : options.block ? `, block ${options.block}` : ''}.` : ''}` }),
    section('What should the assistant see?', contextRow), section('What do you want?', el('div', { className: 'hum-row' }, [earlier]), earlierNote, prompt, el('label', { className: 'tx-opt' }, [strict, el('span', {}, [el('b', { textContent: 'Strict mode' }), el('small', { textContent: 'Off by default: the assistant asks before accessing anything outside this task’s work folder. Turn on to keep the current strict boundary.' })])]), el('label', { className: 'tx-opt ai-model-choice' }, [el('span', {}, [el('b', { textContent: 'Model for this request' }), el('small', { textContent: 'This choice lasts for this request only.' })]), model, effort]), usage, el('div', { className: 'hum-row' }, [hum.button, hum.chips]), el('div', { className: 'hum-row' }, [offer.add, offer.record, offer.pick, offer.chips]), el('small', { className: 'tx-hint', textContent: 'Drag a sound onto this window, pick one, or record one: the assistant may use it, and you decide when you accept whether it comes along into the project.' }), el('small', { className: 'tx-hint', textContent: 'Hum or sing the notes you mean: AMPLE finds them (an attempt), you hear and approve them, and the assistant gets the melody and your raw recording.' })), section('Scope', scope, note, ...(selectionPreview ? [selectionPreview] : [])));
  showSelectionPreview();
  foot.append(go, el('button', { type: 'button', textContent: 'Cancel', onclick: () => box.close() }), el('span', { className: 'note', textContent: 'The assistant works in a temporary copy of the files in scope.' }));
  prompt.focus();
  if (prompt.dataset.defaulted === '1') prompt.select();
}

/** The bottom Assistant tab: project task history plus the existing full Codex composer and conversation view. */
export async function renderAssistantPanel() {
  const host = document.getElementById('b-assistant');
  if (!host) return;
  host.replaceChildren(el('div', { className: 'assistant-panel-head' }, [el('div', { className: 'assistant-panel-brand', title: 'Conversation archiving is optional; task proposals and render metadata are archived separately.' }, [el('span', { className: 'assistant-logo', innerHTML: AI_ASSISTANT_LOGO_SVG, 'aria-label': 'Assistant' }), el('p', { className: 'note', textContent: 'Task proposals stay available. Full conversation archiving is optional per session.' })]), el('span', { className: 'sp' }), el('button', { type: 'button', className: 'primary', 'data-icon': '＋', 'aria-label': 'New session', textContent: 'New session...', onclick: async () => {
    const nameInput = el('input', { type: 'text', value: 'New song idea', 'aria-label': 'Session name' });
    const archiveInfo = 'On: everything you and the assistant say in this session is archived in AMPLE Redux, so you can read it back later. Off (the default): only the task proposals and the render details are kept; the conversation itself is not stored. You can change this per session.';
    const archiveBox = el('input', { type: 'checkbox', className: 'switch-check', 'aria-label': 'Keep the full conversation in AMPLE Redux', title: archiveInfo });
    if (!(await ask('New session', 'Name this assistant session.', [{ label: 'Create', value: true, primary: true }, { label: 'Cancel', value: false }], [nameInput, el('label', { className: 'switch-row', title: archiveInfo }, [archiveBox, el('span', { textContent: 'Keep the full conversation in AMPLE Redux for this session' })])]))) return;
    const title = nameInput.value;
    if (!title.trim()) return;
    const archiveConversation = archiveBox.checked;
    try { const session = await api.post('/api/ai/sessions', { title: title.trim(), archiveConversation }); renderAssistantPanel(); openAiPrompt({ sessionId: session.id, title: session.title, kind: 'custom', scope: 'project', hint: 'What would you like the assistant to do in this project?' }); }
    catch (error) { toast(error.message || String(error)); }
  } }), el('button', { type: 'button', 'data-icon': '↻', 'aria-label': 'Refresh', textContent: 'Refresh', title: 'Reload the session list', onclick: () => renderAssistantPanel() })]));
  const search = el('input', { type: 'search', className: 'assistant-session-search', placeholder: 'Search conversations...', 'aria-label': 'Search assistant conversations' });
  const list = el('div', { className: 'assistant-session-list', role: 'list' }, [el('p', { className: 'note', textContent: 'Loading sessions...' })]);
  // the tabs: the chats that are not archived, only those of Koda, only those that are not, and the archived ones (with what can still be resumed)
  const filters = [['active', 'Active'], ['koda', '♪ Koda'], ['other', 'Not Koda'], ['archived', 'Archived']];
  let filter = 'active';
  try { filter = sessionStorage.getItem('ample.assistant.filter') || 'active'; } catch { /* storage can be blocked */ }
  const tabs = el('div', { className: 'assistant-session-tabs', role: 'tablist' }, filters.map(([key, label]) => el('button', { type: 'button', role: 'tab', 'data-filter': key, className: 'assistant-session-tab', textContent: label, onclick: () => { filter = key; try { sessionStorage.setItem('ample.assistant.filter', key); } catch { /* storage can be blocked */ } apply(); } })));
  host.append(tabs, search, list);
  const shows = (card) => (filter === 'archived' ? card.dataset.archived === '1' : card.dataset.archived !== '1' && (filter === 'active' || (filter === 'koda' ? card.dataset.koda === '1' : card.dataset.koda !== '1')));
  const apply = () => {
    const query = search.value.trim().toLowerCase();
    let matches = 0;
    list.querySelectorAll('.assistant-session-card').forEach((card) => { card.hidden = !(shows(card) && card.dataset.search.includes(query)); if (!card.hidden) matches++; });
    const empty = list.querySelector('.assistant-session-empty');
    if (empty) empty.hidden = matches > 0;
    tabs.querySelectorAll('.assistant-session-tab').forEach((b) => { const on = b.dataset.filter === filter; b.classList.toggle('on', on); b.setAttribute('aria-selected', String(on)); const count = [...list.querySelectorAll('.assistant-session-card')].filter((card) => { const keep = filter; filter = b.dataset.filter; const r = shows(card); filter = keep; return r; }).length; b.dataset.count = String(count); });
  };
  search.oninput = apply;
  try {
    const response = await api.get('/api/ai/sessions');
    if (!host.isConnected) return;
    list.replaceChildren();
    if (!Array.isArray(response) || !response.length) {
      search.hidden = true;
      list.append(el('p', { className: 'note', textContent: 'No assistant sessions yet. Choose “New session” to start a conversation.' }));
      return;
    }
    [...response].sort((a, b) => new Date(b.updated) - new Date(a.updated)).forEach((session) => {
      const ordered = [...(session.tasks || [])].sort((a, b) => new Date(b.updated) - new Date(a.updated));
      const recent = ordered[0];
      const isKoda = (session.tasks || []).some((task) => task.kind === 'koda') || session.title === 'Koda';
      // archived: the conversation ended (archived, rejected, accepted or failed); it may still be resumable while its thread and working copy are kept
      const archived = !!recent && (recent.archived || ['cancelled', 'rejected', 'accepted', 'failed'].includes(recent.status));
      const resume = recent?.resume === 'can' ? (archived ? 'can be resumed' : 'paused: can be resumed') : archived ? 'ended: cannot be resumed' : '';
      const open = el('button', { type: 'button', className: 'assistant-session' + (isKoda ? ' is-koda' : ''), onclick: () => recent ? openAiTask(recent.id, { sessionId: session.id }) : openAiPrompt({ sessionId: session.id, title: session.title, kind: 'custom', scope: 'project', hint: 'What would you like the assistant to do in this project?' }) }, [
        el('span', { className: 'assistant-session-title' + (isKoda ? ' is-koda' : ''), textContent: (isKoda ? '♪ ' : '') + session.title, title: isKoda ? 'Asked through Koda' : '' }),
        el('span', { className: 'assistant-session-meta', textContent: recent ? `${recent.status === 'cancelled' ? 'archived' : recent.status} · ${recent.prompt}` : 'No requests yet' }),
        ...(resume ? [el('span', { className: 'assistant-session-resume ' + (recent.resume === 'can' ? 'can' : 'no'), textContent: resume })] : []),
        el('span', { className: 'assistant-session-meta', textContent: `${(session.tasks || []).length} request(s) · updated ${new Date(session.updated).toLocaleString()}` }),
      ]);
      const archive = el('button', { type: 'button', className: 'assistant-session-action', textContent: session.archiveConversation ? '◉' : '◎', title: session.archiveConversation ? 'Conversation archiving is on; click to turn it off' : 'Turn on conversation archiving', 'aria-label': session.archiveConversation ? 'Turn off conversation archiving' : 'Turn on conversation archiving', onclick: async () => { const enabled = !session.archiveConversation; let includeEarlier = false; if (enabled) { const pick = await ask('Conversation archiving', 'Also include earlier messages still available in this session?', [{ label: 'Include earlier messages', value: 'earlier', primary: true }, { label: 'From now on only', value: 'now' }, { label: 'Cancel', value: false }]); if (!pick) return; includeEarlier = pick === 'earlier'; } try { await api.patch(`/api/ai/sessions/${session.id}`, { archiveConversation: enabled, includeEarlier }); renderAssistantPanel(); } catch (error) { toast(error.message || String(error)); } } });
      const rename = el('button', { type: 'button', className: 'assistant-session-action', textContent: '✎', 'aria-label': 'Rename conversation', title: 'Rename conversation', 'aria-label': `Rename ${session.title}`, onclick: async () => { const nameInput = el('input', { type: 'text', value: session.title, 'aria-label': 'Session name' }); if (!(await ask('Rename session', 'Give this assistant session a new name.', [{ label: 'Rename', value: true, primary: true }, { label: 'Cancel', value: false }], [nameInput]))) return; const title = nameInput.value; if (!title.trim()) return; try { await api.patch(`/api/ai/sessions/${session.id}`, { title: title.trim() }); renderAssistantPanel(); } catch (error) { toast(error.message || String(error)); } } });
      const remove = el('button', { type: 'button', className: 'assistant-session-action danger', textContent: '×', 'aria-label': 'Delete conversation', title: 'Delete conversation', 'aria-label': `Delete ${session.title}`, onclick: async () => { if (!(await ask('Delete conversation', `Delete "${session.title}"? This cannot be undone.`, [{ label: 'Delete', value: true, primary: true }, { label: 'Cancel', value: false }]))) return; try { await api.delete(`/api/ai/sessions/${session.id}`); renderAssistantPanel(); } catch (error) { toast(error.message || String(error)); } } });
      const history = ordered.length > 1 ? el('details', { className: 'assistant-session-history' }, [el('summary', { textContent: `${ordered.length - 1} earlier request(s)` }), ...ordered.slice(1).map((task) => el('button', { type: 'button', textContent: `${task.status} · ${(task.prompt || '').replace(/\s+/g, ' ').slice(0, 110)}`, title: task.prompt, onclick: () => openAiTask(task.id, { sessionId: session.id }) }))]) : el('span');
      list.append(el('div', { className: 'assistant-session-card', role: 'listitem', 'data-search': `${session.title} ${ordered.map((task) => task.prompt || '').join(' ')}`.toLowerCase(), 'data-koda': isKoda ? '1' : '0', 'data-archived': archived ? '1' : '0' }, [el('div', { className: 'assistant-session-row' }, [open, archive, rename, remove]), history]));
    });
    list.append(el('p', { className: 'assistant-session-empty note', textContent: 'No conversations here.', hidden: true }));
    search.hidden = false;
    apply();
  } catch (error) {
    list.replaceChildren(el('p', { className: 'note', textContent: `Could not load assistant sessions: ${error.message || error}` }));
  }
}

// ---- the proposal wizard ----

/** The steps of a build of the proposal as the real build shows them: how many blocks are done, what is made now and how far each step of it is. */
export function buildSteps(build) {
  const p = build?.progress || { done: 0, total: 0, running: [] };
  const pct = p.total ? Math.min(100, Math.round((p.done / p.total) * 100)) : 0;
  const names = (p.running || []).map((r) => r.block);
  const root = el('div', { className: 'ai-steps', id: 'ai-steps', role: 'status' }, [
    el('div', { className: 'ai-steps-head' }, [el('span', { className: 'ai-spin', 'aria-hidden': 'true' }), el('b', { textContent: build?.only ? `Building ${build.only} and what it needs` : 'Checking the build' }), el('span', { className: 'dim', textContent: ` ${p.done}/${p.total} blocks${names.length ? ' · ' + names.slice(0, 2).join(', ') + (names.length > 2 ? ' +' + (names.length - 2) : '') : ''}` })]),
    el('div', { className: 'ai-bar' }, [el('i', { style: `width:${pct}%` })]),
    ...(p.running || []).slice(0, 3).map((r) => el('div', { className: 'ai-step' }, [el('span', { textContent: r.block }), el('span', { className: 'dim', textContent: (r.steps || []).map((s) => s.name + (s.fraction != null ? ' ' + Math.round(s.fraction * 100) + '%' : '')).join(' · ') || Math.round((r.fraction || 0) * 100) + '%' })])),
  ]);
  return root;
}

/** The one player of the AI proposal: whatever is heard (the master, a block) comes through it, so starting one thing stops the other, and Stop stops it. */
const preview = { audio: registerPlaybackAudio(new Audio()), url: '', label: '', listeners: new Set() };
preview.audio.preload = 'metadata';
for (const ev of ['play', 'pause', 'ended', 'timeupdate', 'loadedmetadata']) preview.audio.addEventListener(ev, () => preview.listeners.forEach((fn) => fn()));
// the Stop button of the header knows when the proposal is heard
for (const ev of ['play', 'pause', 'ended']) preview.audio.addEventListener(ev, () => { S.aiPlaying = !preview.audio.paused && !preview.audio.ended; notify(); });
export function stopPreview() { preview.audio.pause(); preview.audio.removeAttribute('src'); preview.audio.load(); preview.url = ''; document.querySelectorAll('.ai-peek-audio').forEach((a) => { a.pause(); a.removeAttribute('src'); a.load(); }); }
export function setPreviewLoop(on) { preview.audio.loop = on; document.querySelectorAll('.ai-peek-audio').forEach((audio) => { audio.loop = on; }); }
export const previewPlaying = () => !preview.audio.paused;
const hear = { want: false, started: false, timer: 0 };
/**
 * The pointer is on a change of a proposal (on = true) or leaves it: when the proposal has been built already, a piece of how it sounds plays (a few seconds). Nothing is built for this, and a preview that is
 * already playing is left alone; without a build there is only the visual information.
 */
export async function hearProposal(taskId, on) {
  clearTimeout(hear.timer);
  const stopHeard = () => { if (hear.started) { stopPreview(); hear.started = false; } };
  hear.want = on;
  if (!on) { stopHeard(); return; }
  if (!taskId || previewPlaying()) return;
  try {
    const t = await api.get(`/api/ai/tasks/${taskId}`);
    if (!hear.want || t.build?.status !== 'ok' || previewPlaying()) return;
    await playPreview(proposalAudioUrl(taskId, t.build.version, t.build.only || null), 'the proposal');
    hear.started = true;
    hear.timer = setTimeout(stopHeard, 4000);
  } catch { /* visual information only */ }
}
/** The block as it is in the song now (its current take), to compare with a variant; false when it has not been built yet. */
export async function hearOriginal(block) {
  const take = S.build.takes[block];
  if (!take) return false;
  stopPreview();
  await playPreview(`/api/takes/${encodeURIComponent(take)}/download?format=wav`, `${block} (original)`).catch(() => toast('Press play in the bar to listen.'));
  return true;
}
const proposalAudioUrl = (taskId, version, block) => `/api/ai/tasks/${taskId}/audio?${block ? `block=${encodeURIComponent(block)}&` : ''}v=${version}`;
async function playPreview(url, label) {
  preview.audio.loop = playerState().loop;
  document.querySelectorAll('.ai-peek-audio').forEach((a) => a.pause());
  if (preview.url !== url) { preview.audio.src = url; preview.url = url; }
  preview.label = label || '';
  preview.listeners.forEach((fn) => fn());
  await preview.audio.play();
}

const peaksCache = new Map();
let peaksQueue = Promise.resolve();
async function peaksOfUrl(url, bins = 600) {
  if (peaksCache.has(url)) return peaksCache.get(url);
  const pending = peaksQueue.then(() => api.get(`${url}${url.includes('?') ? '&' : '?'}peaks=${bins}`)).then((data) => ({ peaks: Array.from({ length: data.points }, (_, i) => Math.max(Math.abs(data.peaks[i * 2]), Math.abs(data.peaks[i * 2 + 1]))), seconds: data.seconds }));
  peaksQueue = pending.catch(() => {});
  peaksCache.set(url, pending);
  try { return await pending; } catch (error) { peaksCache.delete(url); throw error; }
}

/** A waveform of what the proposal sounds like with a play and stop button, a playhead and seeking, for the proposal window and for the bar of the read-only preview. */
export function waveformPlayer(url, label) {
  const canvas = el('canvas', { width: 600, height: 44, className: 'ai-wave', 'aria-label': 'Waveform of ' + label });
  const play = el('button', { type: 'button', className: 'ai-wave-play', textContent: '▶', 'aria-label': 'Listen', title: 'Listen' });
  const stopBtn = el('button', { type: 'button', textContent: '■', 'aria-label': 'Stop', title: 'Stop', onclick: () => stopPreview() });
  const time = el('span', { className: 'ai-wave-time dim', textContent: '' });
  let peaks = null;
  const here = () => preview.url === url;
  const draw = () => {
    const g = canvas.getContext('2d'), w = canvas.width, h = canvas.height;
    g.clearRect(0, 0, w, h);
    const color = getComputedStyle(document.documentElement).getPropertyValue('--acc').trim() || '#4da3ff';
    if (peaks) { const top = Math.max(...peaks.peaks), scale = top > 0 ? Math.min(1000, 0.9 / top) : 1; g.fillStyle = color; peaks.peaks.forEach((p, i) => { const bar = Math.max(1, p * scale * (h - 4)); g.fillRect((i / peaks.peaks.length) * w, (h - bar) / 2, Math.max(1, w / peaks.peaks.length - 0.5), bar); }); }
    if (here() && preview.audio.duration) { g.fillStyle = 'rgba(255,255,255,.9)'; g.fillRect((preview.audio.currentTime / preview.audio.duration) * w - 1, 0, 2, h); }
  };
  const sync = () => { play.textContent = here() && !preview.audio.paused ? '❚❚' : '▶'; time.textContent = peaks ? (here() ? `${preview.audio.currentTime.toFixed(1)} / ` : '') + peaks.seconds.toFixed(1) + ' s' : ''; draw(); };
  play.onclick = async () => { try { if (here() && !preview.audio.paused) preview.audio.pause(); else await playPreview(url, label); } catch (error) { toast(error.message || String(error)); } };
  canvas.onclick = async (ev) => { const r = canvas.getBoundingClientRect(), f = (ev.clientX - r.left) / r.width; try { if (!here()) await playPreview(url, label); if (preview.audio.duration) preview.audio.currentTime = f * preview.audio.duration; } catch (error) { toast(error.message || String(error)); } };
  const root = el('div', { className: 'ai-waveplayer' }, [play, stopBtn, canvas, time]);
  preview.listeners.add(sync);
  const watch = new MutationObserver(() => { if (!root.isConnected) { preview.listeners.delete(sync); watch.disconnect(); } });
  watch.observe(document.body, { childList: true, subtree: true });
  peaksOfUrl(url).then((p) => { peaks = p; sync(); }).catch(() => { time.textContent = 'no waveform'; });
  sync();
  return root;
}

// ---- the way back: the bar of the read-only preview and the pill of a task that is not on the screen ----

/** Shows (or removes) the bar under the banner while a proposal is looked at in the real views: the way back, build, listen with a waveform, stop. */
export function syncPeekBar() {
  const host = document.getElementById('banner');
  let bar = document.getElementById('ai-peek-bar');
  if (!S.peek?.ai) { bar?.remove(); return; }
  const taskId = S.peek.ai;
  if (bar && bar.dataset.task === taskId) return;
  bar?.remove();
  bar = el('div', { id: 'ai-peek-bar', className: 'ai-peek-bar', 'data-task': taskId });
  const slot = el('div', { className: 'ai-peek-wave' });
  const fill = async () => {
    const t = await api.get(`/api/ai/tasks/${taskId}`).catch(() => null);
    slot.replaceChildren(t?.build?.audio && !t.build.only ? waveformPlayer(proposalAudioUrl(taskId, t.build.version), 'the proposal') : el('span', { className: 'dim', textContent: t?.build?.status === 'running' || t?.build?.status === 'pending' ? 'Building the proposal...' : 'Press Build to hear the proposal (it is built in its own temporary project).' }));
  };
  const barInput = el('input', { id: 'ai-peek-msg', type: 'text', placeholder: 'e.g. too loud: make it 20% quieter', 'aria-label': 'Message to Assistant', autocomplete: 'off', className: 'ai-peek-msg' });
  const barHum = humAttachment();
  const barOffer = offerAttachment({ dropTarget: bar });
  const barSend = el('button', { id: 'ai-peek-send', type: 'button', textContent: 'Send' });
  const barStatus = el('span', { className: 'dim ai-peek-status' });
  const sendFromBar = async () => {
    const text = barInput.value.trim();
    if (!text) return;
    barSend.disabled = true;
    try {
      stopPreview();
      await api.post(`/api/ai/tasks/${taskId}/message`, { text, hums: barHum.take(), offered: barOffer.take() });
      barInput.value = '';
      barStatus.textContent = 'The assistant is changing the proposal...';
      for (let i = 0; i < 1200; i++) {
        await new Promise((r) => setTimeout(r, 1000));
        const t = await api.get(`/api/ai/tasks/${taskId}`);
        if (t.status === 'failed' || t.status === 'cancelled') { barStatus.textContent = t.status === 'cancelled' ? 'This conversation was archived.' : 'The assistant stopped: ' + (t.error || t.status); return; }
        if (t.status === 'ready') break;
      }
      barStatus.textContent = 'Updated. Building and playing it...';
      const refreshed = await api.get(`/api/ai/tasks/${taskId}`);
      const song = refreshed.outputSong || refreshed.build?.song || refreshed.song || S.path;
      await enterAiPeek(taskId, song);
      await buildAndListen(taskId, undefined, true);
      barStatus.textContent = 'Playing the new proposal. Say what to change next, or go back to accept.';
      await fill();
    } catch (error) { barStatus.textContent = error.message || String(error); } finally { barSend.disabled = false; barInput.focus(); }
  };
  barSend.onclick = sendFromBar;
  barInput.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); sendFromBar(); } };
  bar.append(
    el('b', { textContent: '✦ Preview of the assistant proposal (read-only)' }),
    el('button', { type: 'button', className: 'primary', id: 'ai-peek-back', textContent: '← Back to the proposal', title: 'Leave the preview and open the proposal window again (to accept or reject it)', onclick: () => { stopPreview(); import('./store.js').then((m) => m.leavePeek()).then(() => openAiTask(taskId)); } }),
    el('button', { type: 'button', textContent: 'Build and listen', title: 'Build the proposal in its temporary project and play it', onclick: async () => { await buildAndListen(taskId); await fill(); } }),
    slot,
    el('div', { className: 'ai-peek-say' }, [barHum.button, barOffer.add, barOffer.record, barOffer.pick, barHum.chips, barOffer.chips, barInput, barSend, barStatus]));
  host.after(bar);
  fill();
  // while Codex works again or a new build runs, nothing of the preview can be played: it stops, the controls are off and the bar says why
  let wasBusy = false;
  let peekPolling = false;
  const watch = setInterval(async () => {
    if (!bar.isConnected) { clearInterval(watch); document.body.classList.remove('ai-busy'); return; }
    if (peekPolling) return;
    peekPolling = true;
    let t;
    try { t = await api.get(`/api/ai/tasks/${taskId}`); } catch { return; } finally { peekPolling = false; }
    if (!bar.isConnected) return;
    if (!t) return;
    const working = t.status !== 'ready';
    const busy = working || ['pending', 'running'].includes(t.build.status);
    if (busy) {
      stopPreview();
      barStatus.replaceChildren(document.createTextNode(working ? 'The assistant is changing the proposal: the preview is disabled until the new build is ready.' : 'A new build of the proposal is in progress: the preview is disabled until it is ready.'));
      if (!working) barStatus.append(buildSteps(t.build));
    } else if (wasBusy) {
      barStatus.textContent = 'The new build is ready.';
      enterAiPeek(taskId, t.outputSong || t.build?.song || t.song || S.path).then(() => fill());
    }
    wasBusy = busy;
    document.body.classList.toggle('ai-busy', busy);
    bar.classList.toggle('busy', busy);
    bar.querySelectorAll('button').forEach((b) => { if (!['ai-peek-back', 'ai-peek-send'].includes(b.id)) b.disabled = busy; });
  }, 1500);
}

let activeTask = null, pillTimer = 0;
/** A small pill in the corner for an AI task that is working or waiting for a decision, so the window can always be opened again. */
export function trackTask(id) {
  activeTask = id;
  clearInterval(pillTimer);
  let busy = false;
  const tick = async () => {
    if (busy) return;
    if (document.getElementById('ai-task')?.open || document.getElementById('ai-prompt')?.open || S.peek?.ai) { document.getElementById('ai-pill')?.remove(); return; }
    busy = true;
    try {
    let pill = document.getElementById('ai-pill');
    const requested = activeTask;
    const t = activeTask ? await api.get(`/api/ai/tasks/${activeTask}?since=${Number.MAX_SAFE_INTEGER}&diffs=0`).catch(() => null) : null;   // the pill needs the state only, not the log or diffs
    if (activeTask !== requested) return;
    const open = document.getElementById('ai-task')?.open || document.getElementById('ai-prompt')?.open;
    if (!t || ['rejected', 'cancelled'].includes(t.status) || t.applied || S.peek?.ai || open) { pill?.remove(); if (!t || t.applied || t.status === 'rejected') { activeTask = null; clearInterval(pillTimer); } return; }
    const koda = t.kind === 'koda';   // asked through Koda: its own icon and name
    const who = koda ? 'Koda' : 'AI';
    const label = ['queued', 'preparing', 'running', 'validating', 'waiting-approval'].includes(t.status) ? (t.status === 'waiting-approval' ? `${who}: waiting for your answer` : `${who}: working...`) : t.status === 'failed' ? `${who}: the task failed` : koda ? 'Koda: answer ready' : 'AI: proposal ready';
    if (!pill) { pill = el('button', { id: 'ai-pill', type: 'button', className: 'ai-pill', onclick: () => openAiTask(activeTask) }); document.body.append(pill); }
    pill.textContent = (koda ? '♪ ' : '✦ ') + label + ' (open)';
    pill.classList.toggle('koda', koda);
    pill.dataset.state = t.status;
    } finally { busy = false; }
  };
  pillTimer = setInterval(tick, 2500);
  tick();
}

/** Opens the Assistant tab of the bottom panel and the conversation of this task in it (Koda sends the person here to look at what was done). */
export async function showInAssistant(taskId) {
  const { setBottomTab } = await import('./panels.js');
  setBottomTab('assistant');
  await renderAssistantPanel();
  openAiTask(taskId);
}

/** Builds the proposal (the whole song, or one block with what it needs) in its temporary project and plays the result; nothing in the real project is built or changed. */
export async function buildAndListen(taskId, block, force = false, signal = null) {
  if (!block) { stopPreview(); stop(); }
  const gone = () => !!signal?.aborted;   // cancelled by the person: the wait stops and the result is not played (the build itself ends in the background)
  if (!force && document.body.classList.contains('ai-busy')) { toast('A new build of the proposal is in progress: wait until it is ready.'); return; }
  try {
    stopPreview();
    toast(block ? `Building ${block} of the proposal...` : 'Building the proposal...');
    // the first check of the proposal may still be running: wait for it, then build what was asked
    for (let i = 0; i < 300 && !gone(); i++) {
      const now = await api.get(`/api/ai/tasks/${taskId}`);
      if (!['pending', 'running'].includes(now.build.status)) break;
      await new Promise((r) => setTimeout(r, 1000));
    }
    if (gone()) return;

    await api.post(`/api/ai/tasks/${taskId}/build`, block ? { block } : {});
    for (let i = 0; i < 600; i++) {
      await new Promise((r) => setTimeout(r, 1000));
      if (gone()) return;
      const t = await api.get(`/api/ai/tasks/${taskId}`);
      if (gone()) return;
      if (!['pending', 'running'].includes(t.build.status)) {
        await applyAiBuild(t);
        if (t.build.status !== 'ok') { toast(`The proposal does not build: ${t.build.message}`); return; }
        const url = proposalAudioUrl(taskId, t.build.version, block && t.build.only === block ? block : null);
        await playPreview(url, block || 'the proposal').catch(() => toast('Press play in the bar to listen.'));
        document.getElementById('ai-peek-bar')?.remove();
        syncPeekBar();
        toast(t.build.message);
        return;
      }
    }
    toast('The build of the proposal takes too long.');
  } catch (error) { toast(error.message || String(error)); }
}

export function openAiTask(id, options = {}) {
  trackTask(id);
  const { box, body, foot, head } = dialogBox('Assistant proposal', 'ai-task');
  body.addEventListener('wheel', (event) => {
    if (event.ctrlKey || !event.deltaY) return;
    const inner = event.target.closest?.('.ai-log,.ai-output-pane');
    if (!inner || !body.contains(inner)) return;
    const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? inner.clientHeight : 1);
    const remaining = delta < 0 ? inner.scrollTop : inner.scrollHeight - inner.clientHeight - inner.scrollTop;
    if (Math.abs(delta) <= Math.max(0, remaining)) return;
    event.preventDefault();
    inner.scrollTop += delta;
    body.scrollTop += Math.sign(delta) * (Math.abs(delta) - Math.max(0, remaining));
  }, { passive: false });
  const taskBadge = el('span', { className: 'ai-badge' });
  head.insertBefore(taskBadge, head.querySelector('.sp'));
  const kodaHook = el('button', { type: 'button', className: 'koda-hook', hidden: true, textContent: '♪ Hook Koda back in', title: 'Koda goes on in this conversation: your next question to Koda continues in this session', onclick: async () => { const m = await import('./koda-server.js'); m.serverBackend.hook(id); toast('Koda is hooked into this session: ask Koda now and the conversation goes on here.'); } });
  head.insertBefore(kodaHook, head.querySelector('.sp'));
  const titleActivityText = el('span');
  const titleActivity = el('span', { className: 'ai-title-activity', role: 'status', 'aria-live': 'polite', hidden: true }, [el('i', { 'aria-hidden': 'true' }), titleActivityText]);
  head.insertBefore(titleActivity, head.querySelector('.sp'));
  let logCache = [], logSeq = 0;
  let timer = 0, current = null, shown = '', tab = 'activity', file = null, scrollActivity = false, lastRelevantThought = '', connectionInterrupted = false;
  let reviewBusy = false;
  const include = new Set();   // the offered audio that comes along into the project when the proposal is accepted
  const moveTicks = new Set();   // the moves the assistant proposed that are made when the proposal is accepted
  const moveSeen = new Set();
  const selectedChanges = new Set();
  const dismissedClips = new Set(), clipUndo = [];
  const seenClipIds = new Set();   // samples that have been on screen in the Output tab (a new one makes the tab pulse)
  let seenLogLength = -1, logMemo = { top: 0, atEnd: true }, outputTop = 0;
  // the Output tab is one element that is never taken out of the page: a playing sample keeps playing while the rest is drawn again or the tab is hidden
  const outputHistory = el('div', { className: 'ai-clip-history', hidden: true });
  const outputSummary = el('div', { className: 'ai-output-summary' });
  const outputScroll = el('div', { className: 'ai-clips-scroll' });
  const outputEmpty = el('p', { className: 'tx-hint', textContent: 'Nothing to hear yet. The samples the assistant presents show up here.' });
  const outputPane = el('div', { className: 'ai-output-pane', id: 'ai-output', hidden: true }, [outputSummary, outputHistory, outputEmpty, outputScroll]);
  const pollController = new AbortController();
  box.addEventListener('close', () => { clearTimeout(timer); pollController.abort(); });
  box.addEventListener('close', () => { dismissedClips.clear(); clipUndo.length = 0; });
  box.addEventListener('keydown', (event) => {
    if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'z' || !clipUndo.length || event.target.closest('input, textarea, [contenteditable="true"]')) return;
    event.preventDefault();
    dismissedClips.delete(clipUndo.pop());
    shown = '';
    draw();
  });
  const post = (path, data) => api.post(`/api/ai/tasks/${id}${path}`, data || {});
  /** Opens the input and output of a tool call: they are not in the log that is polled every second, so they are fetched now. */
  const openToolDetails = async (entry) => {
    if (entry.hasDetails && !entry.details && entry.seq != null) { try { entry.details = (await api.get(`/api/ai/tasks/${id}/log/${entry.seq}`)).details; } catch { /* the entry is no longer kept: the summary is shown */ } }
    showToolDetails(entry);
  };
  // go on with the proposal: tell Codex what to change (also while it works: the message steers it), look, listen, say the next thing, and only then accept
  const msgInput = el('textarea', { id: 'ai-msg-input', rows: 1, placeholder: 'Tell the assistant what to change next... (for example: make it 20% quieter)', 'aria-label': 'Message to Assistant', autocomplete: 'off', spellcheck: true });
  const growMsgInput = () => { const style = getComputedStyle(msgInput), max = parseFloat(style.lineHeight) * 3 + parseFloat(style.paddingTop) + parseFloat(style.paddingBottom) + parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth); msgInput.style.height = 'auto'; msgInput.style.height = Math.min(msgInput.scrollHeight, max) + 'px'; msgInput.style.overflowY = msgInput.scrollHeight > max ? 'auto' : 'hidden'; };
  msgInput.addEventListener('input', growMsgInput);
  const msgSend = el('button', { id: 'ai-msg-send', type: 'button', className: 'primary', textContent: 'Send' });
  const stopNow = el('button', { id: 'ai-stop-now', type: 'button', className: 'danger', textContent: '■', 'aria-label': 'Stop what the assistant is doing', title: 'Stop what the assistant is doing (the conversation stays open: say what to do next)', hidden: true, onclick: () => post('/interrupt').then(poll).catch(say) });
  const archiveNow = el('button', { id: 'ai-archive-now', type: 'button', className: 'quiet', textContent: 'Archive', 'aria-label': 'Archive this conversation', title: 'Archive this conversation: the assistant session ends, the proposal and the working copy are kept and it can be resumed from the Assistant list while they are', hidden: true, onclick: () => post('/archive').then(poll).catch(say) });
  const msgHum = humAttachment();
  const msgOffer = offerAttachment({ dropTarget: box });
  // the advanced menu of the conversation: model, reasoning, compact now, how full the memory is and the usage of the account.
  // A small chip next to it shows the fill and the usage without getting in the way; the menu has the detail and the choices.
  const liveModel = el('select', { id: 'ai-live-model', 'aria-label': 'Model of this conversation' });
  const liveEffort = el('select', { id: 'ai-live-effort', 'aria-label': 'Reasoning effort of this conversation' }, Object.keys(EFFORT_LABELS).map((v) => el('option', { value: v, textContent: EFFORT_LABELS[v] })));
  const ctxBar = el('progress', { id: 'ai-ctx-bar', max: 100, value: 0, style: 'width:100%' });
  const ctxText = el('div', { id: 'ai-ctx-text', className: 'tx-hint' });
  const liveNote = el('div', { id: 'ai-live-note', className: 'tx-hint' });
  const usageRowsBox = el('div', { id: 'ai-usage-rows', className: 'tx-hint', textContent: 'Loading the usage of your account...' });
  const usageAdvice = el('div', { id: 'ai-usage-advice', className: 'tx-hint' });
  const compactBtn = el('button', { type: 'button', id: 'ai-compact', textContent: 'Compact now', title: 'Let the assistant summarize the conversation so far now, between two steps, so it does not happen in the middle of one. Possible at any fill.', onclick: async () => { try { await post('/compact'); await poll(); } catch (e) { say(e); } } });
  const menuField = (label, control) => el('label', { style: 'display:flex;gap:8px;align-items:center;justify-content:space-between;margin:4px 0' }, [el('span', { textContent: label }), control]);
  const modelRetry = el('button', { type: 'button', id: 'ai-models-retry', textContent: 'Retry', onclick: () => loadUsage(true) });
  const modelError = el('div', { id: 'ai-models-error', className: 'tx-warn tx-hint', role: 'alert', hidden: true }, [el('span', { textContent: 'The model list could not be loaded. ' }), modelRetry]);
  const advPanel = el('div', { id: 'ai-advanced-panel', role: 'dialog', tabIndex: -1, 'aria-label': 'Advanced settings of this conversation', style: 'display:none;position:absolute;right:0;bottom:calc(100% + 6px);width:340px;max-width:92vw;z-index:30;padding:12px 14px;border:1px solid var(--line,#555);border-radius:8px;background:var(--panel,#222);box-shadow:0 6px 24px rgba(0,0,0,.35);text-align:left' }, [
    el('b', { textContent: 'Advanced' }),
    menuField('Model', liveModel),
    modelError,
    menuField('Reasoning', liveEffort),
    el('small', { className: 'tx-hint', textContent: 'A change counts from the next turn; the assistant is told about it.' }),
    el('hr'),
    el('b', { textContent: 'Memory of the conversation' }),
    ctxBar, ctxText,
    el('div', { style: 'margin:6px 0' }, [compactBtn]),
    liveNote,
    el('hr'),
    el('b', { textContent: 'Usage of your account' }),
    usageRowsBox, usageAdvice,
  ]);
  const liveChip = el('span', { id: 'ai-live-chip', className: 'tx-hint', title: 'How full the memory of the conversation is and how much usage is left. Open Advanced for the detail.' });
  const advBtn = el('button', { type: 'button', id: 'ai-advanced', textContent: 'Advanced ▾', 'aria-haspopup': 'dialog', 'aria-expanded': 'false' });
  const liveRow = el('div', { className: 'ai-live-settings tx-hint', id: 'ai-live-settings', style: 'display:flex;gap:10px;align-items:center;justify-content:flex-end;position:relative' }, [liveChip, advBtn, advPanel]);
  let usageSummary = { rows: [], low: null, loadedAt: 0 };
  const paintUsage = () => {
    usageRowsBox.replaceChildren();
    if (!usageSummary.rows.length) { usageRowsBox.textContent = 'The usage limits are not available for this account.'; usageAdvice.textContent = ''; return; }
    usageSummary.rows.forEach((r) => usageRowsBox.append(el('div', { textContent: `${r.label}: ${r.left}% left${r.window ? ` · ${r.window} window` : ''}${r.reset ? ` · resets ${r.reset}` : ''}` })));
    usageAdvice.textContent = usageSummary.low != null && usageSummary.low < 20 ? `Usage is getting low (${usageSummary.low}% left): a lighter model or less reasoning uses less.` : '';
    usageAdvice.classList.toggle('tx-warn', usageSummary.low != null && usageSummary.low < 20);
  };
  const loadUsage = async (force) => {
    if (!force && Date.now() - usageSummary.loadedAt < 60000) return;
    usageSummary.loadedAt = Date.now();
    try {
      const r = await api.get('/api/ai/models');
      if (document.activeElement === modelRetry) advPanel.focus();
      modelError.hidden = true;
      if (liveModel.options.length === 0) liveModel.append(el('option', { value: '', textContent: 'server setting' }), el('option', { value: 'default', textContent: 'Assistant default' }), ...(r.models || []).map((m) => el('option', { value: m.id, textContent: m.name || m.id })));
      const buckets = r.usage?.rateLimitsByLimitId || (r.usage?.rateLimits ? { codex: r.usage.rateLimits } : {});
      const rows = Object.entries(buckets).flatMap(([id, limit]) => [limit?.primary, limit?.secondary].filter(Boolean).map((w) => ({
        label: limit.limitName || id,
        left: Math.max(0, 100 - Math.round(w.usedPercent || 0)),
        window: w.windowDurationMins ? (w.windowDurationMins >= 60 && w.windowDurationMins % 60 === 0 ? `${w.windowDurationMins / 60}h` : `${w.windowDurationMins}m`) : '',
        reset: w.resetsAt ? new Date(w.resetsAt * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '',
      })));
      usageSummary = { rows, low: rows.length ? Math.min(...rows.map((r) => r.left)) : null, loadedAt: Date.now() };
    } catch { usageSummary = { rows: [], low: null, loadedAt: 0 }; if (liveModel.options.length === 0) modelError.hidden = false; }
    paintUsage();
    paintChip();
  };
  let lastTask = null;
  const paintChip = () => {
    const ctx = lastTask?.context || {};
    const parts = [];
    if (lastTask?.compacting) parts.push('compacting...');
    else if (ctx.percent != null) parts.push(`memory ${ctx.percent}%`);
    if (usageSummary.low != null) parts.push(`usage ${usageSummary.low}% left`);
    liveChip.textContent = parts.join(' · ');
    liveChip.classList.toggle('tx-warn', Boolean(lastTask?.compacting || (ctx.percent != null && ctx.percent >= 80) || (usageSummary.low != null && usageSummary.low < 15)));
  };
  const closeAdvanced = (returnFocus = true) => { advPanel.style.display = 'none'; advBtn.setAttribute('aria-expanded', 'false'); advBtn.textContent = 'Advanced ▾'; if (returnFocus) advBtn.focus(); };
  advBtn.onclick = () => {
    const open = advPanel.style.display === 'none';
    advPanel.style.display = open ? 'block' : 'none';
    advBtn.setAttribute('aria-expanded', String(open));
    advBtn.textContent = open ? 'Advanced ▴' : 'Advanced ▾';
    if (open) { loadUsage(true); advPanel.focus(); }
  };
  box.addEventListener('keydown', (e) => { if (e.key === 'Escape' && advPanel.style.display !== 'none') { e.preventDefault(); e.stopPropagation(); closeAdvanced(); } });
  box.addEventListener('click', (e) => { if (advPanel.style.display !== 'none' && !liveRow.contains(e.target)) closeAdvanced(false); });
  const changeLive = async () => { try { await post('/assistant', { model: liveModel.value, effort: liveEffort.value }); toast(`Changed: model ${liveModel.selectedOptions[0]?.textContent || 'server setting'}, reasoning ${EFFORT_LABELS[liveEffort.value] || liveEffort.value}. The assistant is told, and works with it from its next turn.`); await poll(); } catch (e) { say(e); } };
  liveModel.onchange = changeLive;
  liveEffort.onchange = changeLive;
  loadUsage(false);
  box.insertBefore(liveRow, foot);
  msgSend.title = 'Send (Ctrl+Enter)';
  msgInput.title = 'Send (Ctrl+Enter)'; msgInput.setAttribute('aria-description', 'Enter adds a new line. Ctrl+Enter or Alt+Enter sends.');
  const msgTools = el('div', { className: 'ai-msg-tools' }, [msgHum.button, msgOffer.add, msgOffer.record, msgOffer.pick, msgHum.chips, msgOffer.chips]);
  const msgCompose = el('div', { className: 'ai-msg-compose' }, [msgInput, stopNow, archiveNow, msgSend]);
  const msgRow = el('div', { className: 'ai-msg', id: 'ai-msg' }, [msgTools, msgCompose]);
  const sendMessage = async (prompt = msgInput.value) => {
    const text = prompt.trim();
    if (!text) return;
    msgSend.disabled = true;
    try { await api.post(`/api/ai/tasks/${id}/message`, { text, hums: msgHum.ids(), offered: msgOffer.ids() }); msgHum.take(); msgOffer.take(); msgInput.value = ''; growMsgInput(); tab = 'activity'; scrollActivity = true; shown = ''; poll(); } catch (error) { say(error); } finally { msgSend.disabled = false; msgInput.focus(); }
  };
  msgSend.onclick = () => sendMessage();
  msgInput.onkeydown = (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.altKey || e.metaKey) && !e.isComposing) { e.preventDefault(); sendMessage(); } };
  box.insertBefore(msgRow, foot);
  // the Codex session of a finished proposal ends after 30 minutes without a message: the proposal stays, and the owner chooses how to go on
  const endedRow = el('div', { className: 'ai-msg tx-warn', id: 'ai-session-ended', style: 'display:none;gap:8px;align-items:center;flex-wrap:wrap' });
  box.insertBefore(endedRow, foot);
  const reconnect = async (fresh) => {
    endedRow.querySelectorAll('button').forEach((button) => { button.disabled = true; });
    try { await post('/resume', fresh ? { fresh: true } : {}); tab = 'activity'; shown = ''; } catch (error) { say(error); }
    poll();
  };

  let pollBusy = false, pollAgain = false;
  /** One poll at a time: a poll that is asked for while another is on its way (after sending a message, for example) runs right after it, so the same log entries are never added twice. */
  async function poll() {
    clearTimeout(timer);
    if (!box.open || !box.isConnected) return;
    if (pollBusy) { pollAgain = true; return; }
    pollBusy = true;
    try { await pollOnce(); } finally {
      pollBusy = false;
      if (pollAgain) { pollAgain = false; poll(); }
    }
  }

  async function pollOnce() {
    try {
      // the log is asked for in pieces: only the entries after the last one this window has (the input and output of tool calls are fetched when one is opened)
      const fresh = await api.get(`/api/ai/tasks/${id}?since=${logSeq}`, pollController.signal);
      if (!box.open || !box.isConnected) return;
      if (fresh.logSeq < logSeq) { logCache = []; logSeq = 0; timer = setTimeout(poll, 0); return; }   // the server started over: ask for everything again
      const known = logSeq;
      logCache = known === 0 ? fresh.log : [...logCache, ...fresh.log.filter((entry) => entry.seq == null || entry.seq > known)].slice(-400);
      logSeq = Math.max(known, fresh.logSeq);
      fresh.log = logCache;
      const wasLive = current && ['queued', 'preparing', 'running', 'waiting-approval', 'validating'].includes(current.status);
      current = fresh;
      connectionInterrupted = false;
      if (!wasLive && ['queued', 'preparing', 'running', 'waiting-approval', 'validating'].includes(current.status)) stopPreview();
      const active = ['queued', 'preparing', 'running', 'waiting-approval', 'validating'].includes(current.status);
      taskBadge.className = `ai-badge${active ? ' working' : ''}`;
      taskBadge.textContent = [current.identity || 'Codex', current.modeLabel, active ? 'working' : ''].filter(Boolean).join(' · ');
      taskBadge.title = `Task status: ${current.status}`;
      const asKoda = current.kind === 'koda';
      kodaHook.hidden = !asKoda;
      taskBadge.classList.toggle('koda', asKoda);
      if (asKoda) taskBadge.textContent = '♪ Koda · ' + taskBadge.textContent;
      const heading = head.querySelector('h2');
      if (heading) { heading.classList.toggle('is-koda', asKoda); heading.textContent = asKoda ? '♪ Koda conversation' : heading.textContent === '♪ Koda conversation' ? 'Assistant proposal' : heading.textContent; }
      body.querySelector('#ai-task-reconnect')?.remove();
    } catch (error) {
      if (!connectionInterrupted) { connectionInterrupted = true; stopPreview(); shown = ''; if (current) draw(); }
      if (!current) body.replaceChildren(el('div', { className: 'tx-warn', id: 'ai-task-reconnect' }, [el('b', { textContent: 'Reconnecting to the assistant task' }), el('p', { textContent: 'The task could not be refreshed. Its conversation and audio remain here while AMPLE retries.' })]));
      else if (!body.querySelector('#ai-task-reconnect')) body.prepend(el('div', { className: 'tx-warn', id: 'ai-task-reconnect' }, [el('b', { textContent: 'Connection interrupted' }), el('p', { textContent: 'The last task update could not be loaded. AMPLE is retrying.' })]));
      timer = setTimeout(poll, 1500);
      return;
    }
    const key = JSON.stringify([current.compacting, current.compactions, current.context?.percent, current.model, current.effort, current.status, current.logSeq ?? current.log.length, current.thought, current.approvals.map((a) => a.id), current.build.status, current.build.message, current.build.version, (current.clips || []).length, Math.floor((current.build.progress?.done || 0)), (current.build.progress?.running || []).map((r) => Math.round((r.fraction || 0) * 10)).join(','), current.stale, current.applied, current.rebase, tab, file]);
    if (key !== shown) { shown = key; draw(); }
    if (['queued', 'preparing', 'running', 'waiting-approval', 'validating'].includes(current.status) || (current.status === 'ready' && ['pending', 'running'].includes(current.build.status)) || current.status === 'ready') timer = setTimeout(poll, current.status === 'ready' && !['pending', 'running'].includes(current.build.status) ? 3000 : 1000);
  }

  let seenApprovals = '';
  /** Draws the task again without losing the place of the reader: the log follows the end while it is at the end, the page keeps its position, and a new question of Codex is scrolled into view. */
  function draw() {
    const log = body.querySelector('#ai-log');
    if (log) logMemo = { top: log.scrollTop, atEnd: log.scrollHeight - log.scrollTop - log.clientHeight < 24 };
    const paneWasHidden = outputPane.hidden;
    if (!paneWasHidden) outputTop = outputPane.scrollTop;
    const bodyTop = body.scrollTop;
    drawNow();
    const nextLog = body.querySelector('#ai-log');
    if (nextLog) nextLog.scrollTop = logMemo.atEnd ? nextLog.scrollHeight : logMemo.top;
    const showOutput = tab === 'output';
    body.classList.toggle('ai-output-mode', showOutput);
    outputPane.hidden = !showOutput;
    if (showOutput && paneWasHidden) outputPane.scrollTop = outputTop;   // coming back to the tab: the place it was left at
    const approvals = (current.approvals || []).map((x) => x.id).join(',');
    const fresh = approvals !== seenApprovals && approvals.length > 0;
    seenApprovals = approvals;
    if (scrollActivity) { body.scrollTop = body.scrollHeight; scrollActivity = false; }
    else if (fresh) body.querySelector('.ai-approval')?.scrollIntoView({ block: 'nearest' });
    else body.scrollTop = bodyTop;
  }

  function drawNow() {
    const t = current;
    for (const child of [...body.children]) if (child !== outputPane) child.remove();
    if (outputPane.parentNode !== body) body.append(outputPane);
    const put = (...nodes) => { const fragment = document.createDocumentFragment(); fragment.append(...nodes); body.insertBefore(fragment, outputPane); };
    if (seenLogLength < 0 || tab === 'activity') seenLogLength = t.log.length;
    if (tab === 'output') for (const c of visibleClips(t)) seenClipIds.add(c.id);
    syncOutput(t);
    foot.replaceChildren();
    if (connectionInterrupted) {
      put(el('div', { className: 'tx-warn', id: 'ai-task-reconnect' }, [el('b', { textContent: 'Connection interrupted' }), el('p', { textContent: 'The task update could not be loaded. Playback and output are hidden until AMPLE confirms the latest result.' })]));
      put(...mainView(t, () => logView(t)));
      foot.append(el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }));
      return;
    }
    const live = ['queued', 'preparing', 'running', 'waiting-approval', 'validating'].includes(t.status);
    const building = t.status === 'ready' && ['pending', 'running'].includes(t.build.status);
    const lastTool = [...t.log].reverse().find((l) => l.kind === 'tool' || l.kind === 'command');
    const latestThought = t.thought || [...t.log].reverse().find((l) => l.kind === 'thought')?.text;
    if (latestThought && (live || building)) lastRelevantThought = latestThought;
    else if (!live && !building) lastRelevantThought = '';
    const action = lastRelevantThought || (live ? { queued: `Waiting in queue (${t.queueAhead} ahead)`, preparing: 'Preparing the proposal', running: lastTool?.text.replace(/^(running|ran): /, '').slice(0, 100) || 'Working on the proposal', 'waiting-approval': 'Waiting for your approval', validating: 'Checking the proposal' }[t.status] : building ? t.build.message || 'Building and checking the proposal' : '');
    titleActivity.hidden = !live && !building;
    titleActivityText.textContent = action;
    titleActivity.title = action;
    msgRow.style.display = t.canMessage ? '' : 'none';
    stopNow.hidden = !live;
    archiveNow.hidden = !!t.archived || ['cancelled', 'failed', 'rejected', 'accepted'].includes(t.status);
    // the advanced menu and its chip: memory fill, compacting, usage
    lastTask = t;
    const ctx = t.context || {};
    ctxBar.value = ctx.percent || 0;
    ctxText.textContent = ctx.percent != null ? `${ctx.percent}% of what the assistant can hold is used${t.compactions ? ` · compacted ${t.compactions}x` : ''}` : 'Not known yet: it appears after the assistant has answered once.';
    liveNote.textContent = t.compacting ? 'The assistant is compacting its memory of this conversation. Wait a moment: it carries on by itself.' : ctx.percent != null && ctx.percent >= 80 ? 'The memory is nearly full: the assistant may compact (summarize) before it answers. You can do it now, between two steps.' : '';
    liveNote.classList.toggle('tx-warn', Boolean(t.compacting || (ctx.percent != null && ctx.percent >= 80)));
    const between = ['ready', 'idle'].includes(t.status);
    compactBtn.disabled = Boolean(t.compacting) || !between;
    compactBtn.title = t.compacting ? 'The assistant is compacting right now.' : between ? 'Let the assistant summarize the conversation so far now, between two steps. Possible at any fill.' : 'Possible between two steps: when the assistant is not working.';
    if (document.activeElement !== liveModel && liveModel.options.length > 2) liveModel.value = t.model || '';
    if (document.activeElement !== liveEffort) liveEffort.value = t.effort || '';
    paintChip();
    if (between) loadUsage(false);
    const ended = t.sessionEnded === true && !t.canMessage;
    endedRow.style.display = ended ? 'flex' : 'none';
    endedRow.textContent = '';
    if (ended) {
      endedRow.append(
        el('span', { textContent: ['accepted', 'rejected'].includes(t.status) ? `This proposal was ${t.status}. The conversation is kept for a day: resume it to go on, and the assistant is told what changed in the project since.` : 'The assistant session of this proposal ended after a while without a message. The proposal is still here.' }),
        t.canResumeThread ? el('button', { type: 'button', id: 'ai-session-resume', textContent: 'Resume the conversation', title: 'Reconnect the assistant to the same thread, with everything it remembers', onclick: () => reconnect(false) }) : '',
        el('button', { type: 'button', id: 'ai-session-new', className: 'primary', textContent: 'Start a new session', title: 'Start a new assistant thread in this working copy; it is told what was asked and what is already changed', onclick: () => reconnect(true) }),
      );
    }
    msgInput.placeholder = live ? 'Steer the assistant while it works... (for example: not that block, use the kick)' : 'Tell the assistant what to change next... (for example: make it 20% quieter)';
    if (live) {
      const busy = { queued: `Waiting for another assistant task to finish (${t.queueAhead} ahead)...`, preparing: 'Preparing a temporary copy...', 'waiting-approval': 'The assistant waits for your approval', validating: 'Checking the proposal...' }[t.status];
      if (busy) put(el('div', { className: 'tx-busy', textContent: busy }));
      for (const a of t.approvals) {
        put(el('div', { className: 'ai-approval', 'data-approval': a.id }, [
          el('b', { textContent: a.kind === 'access' ? 'The assistant asks to write outside the project' : a.kind === 'stop' ? 'STOPPED: the assistant tried to write outside the project' : a.kind === 'move' ? 'The assistant asks to move a file or folder (made when you accept)' : a.kind === 'asset' ? 'The assistant asks to read a source asset' : a.kind === 'download' ? 'The assistant asks AMPLE to download a web address for it' : a.kind === 'installPack' ? 'The assistant asks to install a sample pack into your library' : a.kind === 'browser' ? 'The assistant asks to use the visible browser' : a.kind === 'webSearch' ? 'The assistant asks AMPLE to search the public web' : `The assistant asks to approve a ${a.kind}` }), el('pre', { className: 'tx-pre', textContent: a.summary }), a.detail ? el('p', { className: 'tx-hint', textContent: a.detail }) : el('span'),
          el('div', { className: 'tx-buttons' }, [
            el('button', { type: 'button', className: 'primary', textContent: a.kind === 'browser' ? 'Allow for 30 minutes' : a.kind === 'stop' ? 'Allow once (10 minutes)' : a.kind === 'access' ? 'Allow for the time asked' : a.kind === 'move' ? 'Approve the move' : a.kind === 'asset' ? 'Allow (AMPLE copies it for the assistant)' : a.kind === 'download' ? 'Allow (AMPLE downloads it for the assistant)' : a.kind === 'installPack' ? 'Install it (available to every project)' : a.kind === 'webSearch' ? 'Allow this search' : 'Approve', onclick: () => post('/answer', { approval: a.id, decision: a.kind === 'browser' ? 'accept30' : 'accept' }).then(poll).catch(say) }),
            el('button', { type: 'button', textContent: a.kind === 'browser' ? 'Allow for this task' : a.kind === 'stop' || a.kind === 'access' ? 'Always allow in this project' : a.kind === 'webSearch' ? 'Allow searches for this task' : 'Approve for this task', onclick: () => post('/answer', { approval: a.id, decision: 'acceptForSession' }).then(poll).catch(say) }),
            ...(a.kind === 'browser' ? [el('button', { type: 'button', textContent: 'Always allow in this project', onclick: () => post('/answer', { approval: a.id, decision: 'acceptForProject' }).then(poll).catch(say) })] : []),
            el('button', { type: 'button', textContent: a.kind === 'stop' ? 'Ask it to do it correctly' : 'Deny', onclick: () => post('/answer', { approval: a.id, decision: 'decline' }).then(poll).catch(say) })]),
        ]));
      }
      put(...mainView(t, () => logView(t)));
      return;
    }
    if (t.status === 'failed' || t.status === 'cancelled') {
      put(el('div', { className: 'tx-error', id: 'ai-failed' }, [el('b', { textContent: t.status === 'failed' ? 'The task did not finish' : 'This conversation was archived' }), el('p', { textContent: t.error || (t.resumable ? 'Nothing in the project changed. You can resume the conversation.' : 'Nothing in the project changed.') })]), ...mainView(t, () => logView(t)));
      if (t.resumable) foot.append(el('button', { type: 'button', className: 'primary', textContent: 'Resume conversation', onclick: async () => { try { await post('/resume'); tab = 'activity'; shown = ''; poll(); } catch (error) { say(error); poll(); } } }));
      foot.append(el('button', { type: 'button', textContent: 'Ask again', onclick: () => { box.close(); openAiPrompt({ ...options, hint: t.prompt }); } }), el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }));
      return;
    }
    if (t.status === 'interrupted') {
      put(el('div', { className: 'tx-warn' }, [el('b', { textContent: 'This conversation was interrupted' }), el('p', { textContent: 'The temporary proposal is still here. Resume reconnects the assistant to the saved thread; until then, none of these changes can be accepted.' })]), t.changes?.length ? changesView(t) : logView(t));
      foot.append(t.resumable ? el('button', { type: 'button', className: 'primary', textContent: 'Resume conversation', onclick: async () => { try { await post('/resume'); tab = 'activity'; shown = ''; poll(); } catch (error) { say(error); poll(); } } }) : el('span', { className: 'note', textContent: 'This assistant thread cannot be resumed. Its saved proposal remains available for review.' }), el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }));
      return;
    }
    if (t.status === 'rejected') { put(el('p', { className: 'tx-hint', textContent: 'Rejected. Nothing in the project changed.' })); foot.append(el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() })); return; }
    // a proposal
    const targetSong = t.outputSong || t.build.song || t.song || t.path;
    const proposalInfo = [];
    if (targetSong && (targetSong !== t.song || targetSong !== S.path)) proposalInfo.push(el('p', { className: 'tx-hint', id: 'ai-build-song', textContent: targetSong !== t.song ? `The build targets the new song ${targetSong}; the task started on ${t.song}. Studio currently has ${S.path || 'no song'} open.` : `The build targets ${targetSong}. Studio currently has ${S.path || 'no song'} open.` }));
    if (t.build.audio && t.build.status === 'ok' && !t.build.only) proposalInfo.push(section('Listen to the proposal', waveformPlayer(proposalAudioUrl(id, t.build.version), 'the proposal')));
    if (t.repairs) proposalInfo.push(el('p', { className: 'tx-hint', textContent: `AMPLE sent the assistant back ${t.repairs} time(s) to fix problems it had introduced, before showing you this.` }));
    if (t.offered?.length) proposalInfo.push(offeredSection(t));
    if (t.moves?.length) proposalInfo.push(movesSection(t));
    if (t.stale) proposalInfo.push(staleBanner(t));
    if (t.rebase) proposalInfo.push(el('p', { className: 'tx-hint', textContent: t.rebase }));
    outputSummary.replaceChildren(...(tab === 'output' ? [summarySection(t), ...proposalInfo] : []));
    const bar = tabBar(t, [['activity', 'Chat'], ['output', tabLabel(t)], ['changes', `Changes (${t.changes.length})`], ['checks', 'Checks']]);
    put(bar, ...(tab === 'output' ? [] : [...(tab === 'changes' || tab === 'checks' ? [summarySection(t)] : []), ...proposalInfo, tab === 'changes' ? changesView(t) : tab === 'checks' ? checksView(t) : logView(t)]));
    drawFoot(t);
  }

  // The server appends each new clip, so newest first makes the just-rendered sample immediately visible.
  const visibleClips = (t) => (t.clips || []).filter((c) => !dismissedClips.has(c.id)).reverse();
  const tabLabel = (t) => { const count = visibleClips(t).length; return count ? `Output (${count})` : 'Output'; };
  const summarySection = (t) => section('What the assistant says', el('div', { className: 'ai-summary', id: 'ai-summary' }, [renderAiMarkdown(t.summary || 'The assistant gave no explanation.')]));
  /** The tabs of the task: a tab with something the owner has not seen yet (a new sample, new lines in the chat) pulses softly until it is opened. */
  function tabBar(t, names) {
    const newSample = tab !== 'output' && visibleClips(t).some((c) => !seenClipIds.has(c.id));
    const newChat = tab !== 'activity' && seenLogLength >= 0 && t.log.length > seenLogLength;
    return el('div', { className: 'tx-seg' }, names.map(([k, label]) => el('button', {
      type: 'button', textContent: label, 'data-tab': k,
      className: [k === tab ? 'on' : '', (k === 'output' && newSample) || (k === 'activity' && newChat) ? 'pulse' : ''].filter(Boolean).join(' '),
      onclick: () => { tab = k; shown = ''; draw(); },
    })));
  }
  /** Chat and Output as two tabs: the chat is drawn here, the Output pane is shown by draw() when its tab is on. */
  function mainView(t, chat) {
    const bar = tabBar(t, [['activity', 'Chat'], ['output', tabLabel(t)]]);
    if (tab === 'output') return [bar];
    return [bar, chat()];
  }
  /** Brings the cards of the Output pane up to date without touching the ones that are there: they keep playing, and what the owner looks at stays where it is when a card is added or removed. */
  function syncOutput(t) {
    if (connectionInterrupted) {
      outputHistory.hidden = true;
      outputScroll.replaceChildren();
      outputEmpty.hidden = false;
      outputEmpty.textContent = 'Output is hidden until AMPLE reconnects and confirms the latest task result.';
      return;
    }
    outputEmpty.textContent = 'Nothing to hear yet. The samples the assistant presents show up here.';
    const clips = visibleClips(t);
    const shownCards = [...outputScroll.children];
    const byId = new Map(shownCards.map((node) => [node.dataset.clip, node]));
    const paneTop = outputPane.getBoundingClientRect().top;
    const anchor = outputPane.hidden ? null : shownCards.find((node) => node.getBoundingClientRect().bottom > paneTop);
    const anchorTop = anchor ? anchor.getBoundingClientRect().top : 0;
    const wanted = new Set(clips.map((c) => c.id));
    for (const [clipId, node] of byId) if (!wanted.has(clipId)) node.remove();
    let previous = null;
    for (const c of clips) {
      const node = byId.get(c.id) || clipCard(c);
      const other = c.identicalTo ? t.clips.find((x) => x.id === c.identicalTo) : null;
      const sameLabel = c.identicalTo ? `${other?.label || c.identicalTo}` : '';
      if (node.dataset.same !== sameLabel) {
        node.dataset.same = sameLabel;
        node.querySelector('.ai-clip-warn')?.replaceChildren(...(c.identicalTo ? [el('p', { className: 'tx-warn', textContent: `Identical audio to “${sameLabel}”. The label does not change the sound.` })] : []));
      }
      node.classList.toggle('obsolete', !!c.obsoleteReason);
      const obsoleteNote = node.querySelector('.ai-clip-obsolete-reason');   // a fetched sample has none
      if (obsoleteNote) obsoleteNote.textContent = c.obsoleteReason ? `Obsolete: ${c.obsoleteReason}` : '';
      const expected = previous ? previous.nextElementSibling : outputScroll.firstElementChild;
      if (node !== expected) outputScroll.insertBefore(node, expected);
      previous = node;
    }
    if (anchor?.isConnected && !outputPane.hidden) outputPane.scrollTop += anchor.getBoundingClientRect().top - anchorTop;
    outputHistory.hidden = !clipUndo.length;
    outputHistory.replaceChildren(...(clipUndo.length ? [el('span', { className: 'tx-hint', textContent: clips.length ? `${clipUndo.length} hidden · Undo (Ctrl+Z)` : 'All samples hidden · Undo hide (Ctrl+Z)' }), el('button', { type: 'button', textContent: 'Undo hide', onclick: () => { dismissedClips.delete(clipUndo.pop()); shown = ''; draw(); } })] : []));
    outputEmpty.hidden = clips.length > 0 || clipUndo.length > 0;
  }

  const logView = (t) => {
    const entries = [...t.log];
    const resumedDuplicates = new Set();
    let resumed = false, lastChangeGroup = '';
    for (let i = 0; i < entries.length; i++) {
      if (entries[i].kind === 'info' && /conversation resumed after the server restart/i.test(entries[i].text)) resumed = true;
      if (entries[i].kind !== 'change-summary') continue;
      let end = i + 1;
      while (end < entries.length && entries[end].kind === 'change') end++;
      const signature = `${entries[i].text}\n${entries.slice(i + 1, end).map((change) => change.path).join('\n')}`;
      if (resumed && signature === lastChangeGroup) for (let j = i; j < end; j++) resumedDuplicates.add(j);
      lastChangeGroup = signature;
      resumed = false;
    }
    const thought = (t.thought || '').trim();
    if (thought && !entries.some((item) => item.kind === 'thought' && item.text.trim() === thought)) {
      const taskActive = ['queued', 'preparing', 'running', 'waiting-approval', 'validating'].includes(t.status);
      const lastThought = entries.findLastIndex((item) => item.kind === 'thought');
      const lastOperation = entries.findLastIndex((item) => item.kind === 'command' || item.kind === 'tool');
      const commandIsNewer = taskActive && lastOperation > lastThought && entries[lastOperation].kind === 'command' && /^running:/i.test(entries[lastOperation].text);
      const finalReply = taskActive ? -1 : entries.findLastIndex((item) => item.kind === 'assistant');
      const insertAt = taskActive ? commandIsNewer ? lastOperation : entries.length : finalReply >= 0 ? finalReply : entries.length;
      entries.splice(insertAt, 0, { kind: 'thought', text: thought });
    }
    const lastOperation = entries.findLastIndex((item) => item.kind === 'command' || item.kind === 'tool');
    const lastThought = entries.findLastIndex((item) => item.kind === 'thought');
    const lastNonThoughtOutput = entries.findLastIndex((item) => ['assistant', 'command', 'tool', 'user'].includes(item.kind));
    const firstUser = entries.findIndex((item) => item.kind === 'user');
    return el('div', { className: 'ai-log', id: 'ai-log' }, entries.flatMap((l, i) => {
    if (resumedDuplicates.has(i)) return [];
    if (l.kind === 'info' && /^Codex did not change any file\.?$/i.test(l.text.trim())) return [];
      const active = ['queued', 'preparing', 'running', 'waiting-approval', 'validating'].includes(t.status) && ((i === lastOperation && l.kind === 'command' && /^running:/i.test(l.text)) || (i === lastThought && i > lastNonThoughtOutput));
    const text = l.kind === 'user' && i === firstUser && t.prompt.length > l.text.length ? t.prompt : l.text;
    const message = l.kind === 'user' && text.length > 300
      ? el('details', { className: 'ai-log-user-message' }, [el('summary', { textContent: `${text.slice(0, 220).replace(/\s+/g, ' ')}… Show full message` }), el('div', { className: 'ai-log-user-message-text', textContent: text })])
      : el('span', { textContent: text });
    const content = [el('span', { textContent: l.kind }), l.kind === 'assistant' ? renderAiMarkdown(l.text) : message];
    if (l.kind === 'tool' || l.kind === 'command') {
      content[1] = el('span', { className: 'ai-tool-summary' }, [content[1], el('button', { type: 'button', className: 'ai-tool-link', textContent: '↗', 'aria-label': 'Inspect input, output and tool details', title: 'Inspect input, output and tool details', 'aria-label': `Inspect ${l.text}`, onclick: () => openToolDetails(l) })]);
    }
    if (l.kind === 'change-summary') {
      const files = [];
      for (let j = i + 1; j < t.log.length && t.log[j].kind === 'change'; j++) {
        const change = t.log[j];
        files.push(el('button', { type: 'button', className: 'ai-change-link', title: 'Open this file in Changes', onclick: () => { file = change.path; tab = 'changes'; shown = ''; draw(); } }, [el('b', { textContent: change.path }), ...(change.why ? [el('small', { textContent: change.why })] : [])]));
      }
      return [el('div', { className: 'ai-change-group' }, [el('b', { textContent: l.text }), el('div', { className: 'ai-change-files' }, files)])];
    }
    if (l.kind === 'change' && t.log.slice(0, i).some((x) => x.kind === 'change-summary')) return [];
    return l.kind === 'change'
      ? el('button', { type: 'button', className: 'ai-line ai-change', title: 'Open this file in Changes', onclick: () => { file = l.path; tab = 'changes'; shown = ''; draw(); } }, content)
      : [el('div', { className: `ai-line ${l.kind}${active ? ' active' : ''}` }, content)];
    }));
  };

  /** A "Save as..." dropdown of a presented sound: the clip as WAV or MP3 (the server encodes it), and the settings it was rendered with as JSON. */
  function clipSaveMenu(c) {
    const choices = [['', 'Save as...'], ...(c.mediaType === 'audio/mpeg' ? [['mp3', 'MP3 audio (.mp3)']] : [['wav', 'WAV audio (.wav)'], ['mp3', 'MP3 audio (.mp3)']]), ...(c.settings?.length ? [['json', 'Rendered source settings (.json)']] : [])];
    const select = el('select', { className: 'ai-clip-save-select', title: 'Save this sample as a file', 'aria-label': `Save ${c.label} as`, onchange: () => {
      const kind = select.value;
      select.value = '';
      if (!kind) return;
      const link = document.createElement('a');
      const name = String(c.label || c.id).replace(/[^w.-]+/g, '-').replace(/^-+|-+$/g, '') || c.id;
      if (kind === 'json') {
        link.href = URL.createObjectURL(new Blob([JSON.stringify(c.settings, null, 2)], { type: 'application/json' }));
        link.download = `${name}.settings.json`;
      } else {
        link.href = `/api/ai/tasks/${id}/clip/${c.id}?format=${kind}`;
        link.download = `${name}.${kind}`;
      }
      document.body.append(link);
      link.click();
      link.remove();
    } }, choices.map(([value, text]) => el('option', { value, textContent: text })));
    return select;
  }

  /**
   * What a presented sample was made from. A temporary trial lists many edits: the summary line says how many of each kind, the list below it shows them one per line (the lint comments
   * AMPLE adds are only counted), and the rendered source settings are one more level down.
   */
  function clipSettings(c) {
    const trial = (c.trial || []).map((x) => String(x));
    const hasSettings = (c.settings?.length || 0) > 0;
    if (!trial.length && !hasSettings) return el('span');
    const settings = hasSettings ? el('details', { className: 'ai-clip-settings-json' }, [el('summary', { textContent: 'Rendered source settings' }), el('pre', { textContent: JSON.stringify(c.settings, null, 2) })]) : '';
    if (!trial.length) return el('details', { className: 'ai-clip-settings' }, [el('summary', { textContent: 'Rendered source settings' }), el('pre', { textContent: JSON.stringify(c.settings, null, 2) })]);
    const noise = trial.filter((x) => /^add_block:\s*#\s*ample-ignore\b/.test(x)).length;
    const edits = trial.filter((x) => !/^add_block:\s*#\s*ample-ignore\b/.test(x));
    const kinds = [['add_block', 'block(s) added'], ['connect', 'connection(s)'], ['disconnect', 'disconnection(s)'], ['set_param', 'parameter change(s)']];
    const counts = kinds.map(([prefix, label]) => [edits.filter((x) => x.startsWith(prefix + ':')).length, label]).filter(([n]) => n > 0);
    const other = edits.length - counts.reduce((sum, [n]) => sum + n, 0);
    if (other > 0) counts.push([other, 'other edit(s)']);
    const summary = `Temporary trial · ${edits.length} change(s)${counts.length ? ': ' + counts.map(([n, label]) => `${n} ${label}`).join(', ') : ''}`;
    return el('details', { className: 'ai-clip-settings' }, [
      el('summary', { textContent: summary, title: 'Edits applied only to this sample; the proposal itself is not changed' }),
      el('ul', { className: 'ai-trial-list' }, edits.map((x) => el('li', { textContent: x }))),
      noise > 0 ? el('p', { className: 'tx-hint', textContent: `${noise} lint-ignore comment(s) not shown.` }) : '',
      settings,
    ]);
  }

  /** The card of a sound that Codex wants the user to hear: what it is and why, the player, and buttons to answer or directly send an action. */
  function clipCard(c) {
    const say = (text) => { msgInput.value = text; growMsgInput(); msgInput.focus(); };
    if (c.source === 'external') return el('div', { className: 'ai-clip', 'data-clip': c.id }, [
      el('div', { className: 'ai-clip-head' }, [el('b', { textContent: c.label }), el('span', { className: 'dim', textContent: ' · fetched sample' }), el('button', { type: 'button', className: 'ai-clip-dismiss', textContent: '×', 'aria-label': 'Hide this sample', title: 'Hide this sample', 'aria-label': `Hide ${c.label}`, onclick: () => { stopPreview(); dismissedClips.add(c.id); clipUndo.push(c.id); shown = ''; draw(); } })]),
      c.why ? el('p', { className: 'tx-hint', textContent: c.why }) : el('span'),
      el('audio', { controls: true, preload: 'none', src: `/api/ai/tasks/${id}/clip/${c.id}`, 'aria-label': `Listen to ${c.label}`, onplay: (event) => document.querySelectorAll('.ai-clip audio').forEach((other) => { if (other !== event.currentTarget) other.pause(); }) }),
      el('div', { className: 'ai-clip-save' }, [clipSaveMenu(c)]),
      el('div', { className: 'tx-buttons' }, [el('button', { type: 'button', textContent: 'Use this sample', title: 'Add this selection as context for the assistant', onclick: () => say(`I choose the sample “${c.label}” (clip ${c.id}, workspace/${c.workspaceAssetPath}). Test this exact sample in the current proposal; do not make other variants unless needed.`) })]),
    ]);
    const reason = el('p', { className: 'ai-clip-obsolete-reason', role: 'status', textContent: c.obsoleteReason ? `Obsolete: ${c.obsoleteReason}` : '' });
    const clipReference = `sample “${c.label}” (clip ${c.id}${c.source === 'current-rebuilt' ? ', reconstructed from current song text without freeze selections' : c.source === 'current-frozen' ? ', from a frozen selected take' : ''}) for block “${c.block}”`;
    const actions = [
      ['choose', 'Yes, that is it', `I choose the ${clipReference}. Keep this direction for that block and continue the proposal on this basis; do not make another variant unless needed.`],
      ['different', 'No, another one', `The ${clipReference} is not the direction I want. Make and present a clearly different sample for the same block, keeping the surrounding proposal context unless a wider change is needed.`],
      ['more', 'Play me more of it', `Please build and present more audio for the ${clipReference}, extending or continuing that same block so I can hear more of this direction. Keep the current direction.`],
    ];
    const actionButton = ([action, label, prompt]) => el('button', { type: 'button', 'data-clip-action': action, textContent: label, title: 'Send now (Ctrl+click)', 'aria-description': 'Click to add this instruction to the message. Ctrl+click sends it now with this sample and block as context.', onclick: (event) => event.ctrlKey || event.metaKey ? sendMessage(prompt) : say(prompt) });
    return el('div', { className: `ai-clip${c.obsoleteReason ? ' obsolete' : ''}`, 'data-clip': c.id }, [
      el('div', { className: 'ai-clip-head' }, [el('b', { textContent: c.label }), el('span', { className: 'dim', textContent: ` ${c.block} · ${c.source === 'current-rebuilt' ? 'rebuilt from current song text (freeze selections bypassed)' : c.source === 'current-frozen' ? 'current project · frozen selected take' : c.source === 'current' ? 'as your project is now' : 'as in the proposal'} · ${c.from.toFixed(1)}–${c.to.toFixed(1)} s` }), el('button', { type: 'button', className: 'ai-clip-dismiss', textContent: '×', 'aria-label': 'Hide this sample', title: 'Hide this sample', 'aria-label': `Hide ${c.label}`, onclick: () => { stopPreview(); dismissedClips.add(c.id); clipUndo.push(c.id); shown = ''; draw(); } })]),
      c.why ? el('p', { className: 'tx-hint', textContent: c.why }) : el('span'),
      reason,
      el('div', { className: 'ai-clip-warn' }),
      clipSettings(c),
      waveformPlayer(`/api/ai/tasks/${id}/clip/${c.id}`, c.label),
      el('div', { className: 'ai-clip-save' }, [clipSaveMenu(c)]),
      el('div', { className: 'tx-buttons' }, actions.map(actionButton)),
    ]);
  }

  /** The audio the user offered: the ones the proposal uses have to come along; the others can, if the user wants them in the project. Each one can be heard first. */
  /** The moves of files and folders that Codex proposed: nothing moves before Accept, then the ticked ones are made and the references in the songs follow. */
  function movesSection(t) {
    for (const m of t.moves) {
      if (!moveSeen.has(m.id)) { moveSeen.add(m.id); if (m.mode === 'propose' || m.approved) moveTicks.add(m.id); }
      if (m.approved) moveTicks.add(m.id);
    }
    const rows = t.moves.map((m) => {
      const box2 = el('input', { type: 'checkbox', checked: moveTicks.has(m.id), disabled: m.approved || m.applied, 'aria-label': `Move ${m.from}`, onchange: () => { if (box2.checked) moveTicks.add(m.id); else moveTicks.delete(m.id); const action = document.getElementById('ai-accept-moves'); if (action) action.disabled = action.dataset.blocked === 'true' || !t.moves.some((move) => !move.applied && moveTicks.has(move.id)); } });
      const refs = m.references ? `${m.references} line${m.references === 1 ? '' : 's'} in your songs will follow` : 'nothing refers to it';
      const state = m.applied ? 'moved' : m.approved ? 'you approved it' : 'tick to move it at Accept';
      return el('div', { className: 'ai-move-row' }, [box2, el('span', { className: 'ai-move-path', textContent: `${m.from}  →  ${m.to}` }), el('span', { className: 'dim', textContent: ` · ${m.isDir ? 'folder' : 'file'} · ${refs} · ${state}${m.reason ? ` · ${m.reason}` : ''}` })]);
    });
    return section('Moves the assistant proposes', el('p', { className: 'tx-hint', textContent: 'Nothing is moved before you accept. The ticked moves are made after the changes, and every path in your songs and modules that points at a moved file follows. Undo takes them back.' }), ...rows);
  }

  function offeredSection(t) {
    for (const o of t.offered) if (o.used) include.add(o.id);
    const rows = t.offered.map((o) => {
      const box2 = el('input', { type: 'checkbox', checked: include.has(o.id), disabled: o.used, 'aria-label': `Take ${o.name} along`, onchange: () => { if (box2.checked) include.add(o.id); else include.delete(o.id); } });
      const play = el('button', { type: 'button', className: 'mini', textContent: '▶', 'aria-label': 'Listen to it', title: 'Listen to it', onclick: () => previewAttachment(o.id, play) });
      return el('div', { className: 'ai-offered-row' }, [box2, play, el('span', { textContent: `${o.name} · ${o.seconds.toFixed(1)} s · ${(o.size / 1000).toFixed(0)} kB` }), el('span', { className: o.used ? 'tx-done-badge' : 'dim', textContent: o.used ? 'used by the proposal: it has to come along' : 'not used: only if you want it in the project' }), el('code', { className: 'dim', textContent: o.path })]);
    });
    return section('Audio you offered the assistant', el('p', { className: 'tx-hint', textContent: 'These files only exist for this proposal. Tick the ones that should be copied into your project when you accept (the ones the proposal uses have to).' }), ...rows);
  }

  function staleBanner(t) {
    return el('div', { className: 'tx-warn', id: 'ai-stale' }, [
      el('b', { textContent: 'The project changed while the assistant was working' }), el('p', { textContent: `This proposal was made against an older state of: ${t.staleFiles.join(', ')}. It will not be applied blindly.` }),
      el('div', { className: 'tx-buttons' }, [
        el('button', { type: 'button', className: 'primary', textContent: 'Try to rebase', onclick: () => post('/rebase').then(() => { shown = ''; poll(); }).catch(say) }),
        el('button', { type: 'button', textContent: 'Regenerate against the current state', onclick: () => post('/reject').then(() => { box.close(); openAiPrompt({ ...options, hint: t.prompt }); }).catch(say) }),
        el('button', { type: 'button', textContent: 'Review anyway (view only)', onclick: () => toast('You can read the proposal; it cannot be accepted until it is rebased or regenerated.') })]),
    ]);
  }

  function changesView(t) {
    if (t.kind === 'explain' || !t.changes.length) return el('p', { className: 'tx-hint', textContent: t.kind === 'explain' ? 'This was a request for an explanation: nothing is proposed to change.' : 'The assistant did not propose any change.' });
    file = t.changes.some((c) => c.path === file) ? file : t.changes[0].path;
    const list = el('div', { className: 'ai-files' }, t.changes.map((c) => {
      const choose = el('input', { type: 'checkbox', checked: selectedChanges.has(c.path), 'aria-label': `Accept ${c.path} and continue`, onchange: () => { if (choose.checked) selectedChanges.add(c.path); else selectedChanges.delete(c.path); const action = document.getElementById('ai-accept-selected'); if (action) action.disabled = !selectedChanges.size || (t.build.status !== 'ok' && !t.build.skipRequested) || t.stale; } });
      return el('div', { className: `ai-file-row ${c.path === file ? 'on' : ''}` }, [choose, el('button', { type: 'button', className: 'ai-file-pick', onclick: () => { file = c.path; shown = ''; draw(); } }, [el('span', { className: `ai-st ${c.status}`, textContent: c.status === 'added' ? 'A' : c.status === 'deleted' ? 'D' : 'M' }), el('span', { className: 'ai-file-name', title: c.path }, [el('b', { textContent: c.path.slice(c.path.lastIndexOf('/') + 1) }), el('small', { textContent: c.path.includes('/') ? c.path.slice(0, c.path.lastIndexOf('/')) : '' })]), el('small', { className: 'ai-file-count', textContent: `+${c.added} −${c.removed}` })])]);
    }));
    const c = t.changes.find((x) => x.path === file);
    const lines = (c.diff || '').split('\n').map((l) => el('div', { className: `ai-d ${l[0] === '+' ? 'add' : l[0] === '-' ? 'del' : l[0] === '@' ? 'hunk' : ''}`, textContent: l || ' ' }));
    const musical = c.musical ? el('p', { className: 'tx-hint', textContent: `Structure: patterns ${c.musical.patternsBefore} → ${c.musical.patternsAfter}, channels ${c.musical.channelsBefore} → ${c.musical.channelsAfter}, clips ${c.musical.clipsBefore} → ${c.musical.clipsAfter}.` }) : el('span');
    return el('div', { className: 'ai-changes' }, [list, el('div', {}, [c.why ? el('p', { className: 'ai-change-why', textContent: `Why this file changed: ${c.why}` }) : el('span'), musical, el('div', { className: 'ai-diff', id: 'ai-diff' }, lines)])]);
  }

  function checksView(t) {
    const b = t.build;
    const testButton = el('button', { id: 'ai-build-test', type: 'button', className: 'primary', textContent: b.only ? 'Build the whole proposal and listen' : 'Build and test again', disabled: ['pending', 'running'].includes(b.status), onclick: () => { buildAndListen(id); setTimeout(() => { shown = ''; poll(); }, 600); } });
    const parts = [el('div', { className: b.status === 'ok' ? 'tx-done' : b.status === 'failed' ? 'tx-error' : 'tx-warn', id: 'ai-build' }, [el('b', { textContent: b.status === 'skipped' && b.skipRequested ? 'Build not run (as requested)' : ({ ok: 'Build successful', failed: 'Build failed', running: 'Checking the build...', pending: 'Waiting to check the build...', none: 'Nothing to build', skipped: 'Not built (the check could not be set up)' }[b.status] || b.status) }), el('p', { textContent: b.message || '' }), b.failures.length ? el('ul', {}, b.failures.map((f) => el('li', { textContent: f }))) : el('span')])];
    parts.push(el('div', { className: 'tx-buttons' }, [testButton]));
    if (t.diagnostics.length) parts.push(section('Diagnostics', el('ul', { id: 'ai-diagnostics' }, t.diagnostics.map((d) => el('li', { textContent: d })))));
    if (b.audio) parts.push(section('Listen to the proposal', waveformPlayer(proposalAudioUrl(id, b.version), 'the proposal'), el('p', { className: 'tx-hint', textContent: 'The proposal was built in a staged copy; your project is not touched.' })));
    return el('div', {}, parts);
  }

  async function reviewAction(path, data) {
    if (reviewBusy) return;
    reviewBusy = true;
    foot.querySelectorAll('button').forEach((button) => { button.disabled = true; });
    try {
      const result = await post(path, data);
      if (result.accepted) selectedChanges.clear();
      if (result.songText != null) { if (S.path === result.song) await applyText(result.songText); else if (result.accepted) toast('The song ' + result.song + ' is changed on disk; open it to see the change.'); }
      if (result.accepted) await reloadDoc();
      const continuation = result.continuation || result;
      if (continuation.note) toast(continuation.note);
      msgInput.value = ''; tab = 'activity';
    } catch (error) { say(error); }
    finally { reviewBusy = false; shown = ''; poll(); }
  }

  function drawFoot(t) {
    const invalid = t.build.status === 'failed' || t.diagnostics.some((d) => d.includes(' error '));
    const checking = ['pending', 'running'].includes(t.build.status);
    const none = t.kind === 'explain' || !t.changes.length;
    const pendingMoves = t.kind !== 'explain' ? t.moves.filter((move) => !move.applied) : [];
    if (t.applied) {
      foot.append(el('span', { className: 'ai-ok', id: 'ai-accepted', textContent: 'Applied. The song is changed and not saved. Undo (Ctrl+Z)' }), el('button', { type: 'button', textContent: 'Undo the files of this change', title: 'Puts back the files other than the open song', onclick: () => post('/undo').then(async (r) => { if (r.songText != null && S.path === r.song) await applyText(r.songText); await reloadDoc(); toast('Undone.'); shown = ''; poll(); }).catch(say) }), el('button', { type: 'button', className: 'primary', textContent: 'New request in this session', title: 'The accepted assistant thread has ended. Start a new thread with this task’s prompt, result, and changes as context.', onclick: () => { box.close(); openAiPrompt({ sessionId: t.sessionId, title: 'New request in this session', kind: 'custom', scope: 'project', path: t.path, song: t.song, continueFrom: id, hint: 'What would you like to discuss next?' }); } }), el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }));
      return;
    }
    const accept = async (anyway) => {
      try {
        const r = await post('/accept', { anyway, include: [...include], moves: [...moveTicks] });
        if (r.songText != null) { if (S.path === r.song) await applyText(r.songText); else toast(`The song ${r.song} is changed on disk.`); }
        await reloadDoc();
        shown = '';
        poll();
      } catch (error) { say(error); poll(); }
    };
    if (none) {
      if (pendingMoves.length) {
        const acceptMoves = el('button', { id: 'ai-accept-moves', type: 'button', className: 'primary', textContent: 'Accept moves', disabled: reviewBusy || t.status !== 'ready' || invalid || checking || t.stale || !pendingMoves.some((move) => moveTicks.has(move.id)), title: 'Apply only the checked file and folder moves', onclick: () => { acceptMoves.disabled = true; accept(false); } });
        acceptMoves.dataset.blocked = String(reviewBusy || t.status !== 'ready' || invalid || checking || t.stale);
        foot.append(acceptMoves);
      }
      foot.append(el('button', { type: 'button', id: 'ai-continue', className: 'primary', textContent: 'Continue remaining work', disabled: reviewBusy || !t.canMessage, title: 'Finish only missing work in this conversation; do not repeat the original request', onclick: () => reviewAction('/continue', { message: msgInput.value.trim() }) }), el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }));
      return;
    }
    foot.append(
      el('button', { id: 'ai-accept', type: 'button', className: 'primary', textContent: 'Accept', disabled: invalid || checking || t.stale, title: invalid ? 'The proposal has source errors or failed its build' : checking ? 'The proposal is still being checked' : t.stale ? 'The project changed: rebase or regenerate first' : t.build.skipRequested ? 'Accept after source validation without building or rendering' : 'Apply the proposal as one change. Undo (Ctrl+Z)', onclick: () => accept(false) }),
      invalid ? el('button', { id: 'ai-accept-anyway', type: 'button', textContent: 'Accept anyway', title: 'Apply it although the project then does not build right now', disabled: t.stale, onclick: async () => { if (await confirmAnyway(t)) accept(true); } }) : el('span'),
      el('button', { id: 'ai-accept-selected', type: 'button', textContent: 'Accept selected', disabled: reviewBusy || !selectedChanges.size || invalid || checking || t.stale, title: 'Apply the checked files without starting another assistant turn', onclick: () => reviewAction('/accept-selected', { paths: [...selectedChanges], include: [...include], moves: [...moveTicks] }) }),
      el('button', { id: 'ai-accept-selected-continue', type: 'button', textContent: 'Accept selected, then continue', disabled: reviewBusy || !selectedChanges.size || invalid || checking || t.stale || !t.canMessage, title: 'Apply checked files and finish only work not already delivered, keeping the original limits', onclick: () => reviewAction('/accept-selected-continue', { paths: [...selectedChanges], message: msgInput.value.trim(), include: [...include], moves: [...moveTicks] }) }),
      el('button', { id: 'ai-blockview', type: 'button', textContent: 'Preview in blocks', title: 'Shows the proposed song in the real views, read-only, where you can build and listen to single blocks', onclick: async () => { const song = t.outputSong || t.build?.song || t.song || options.song || S.path; box.close(); if (await enterAiPeek(id, song).catch(say)) { document.getElementById('vs-blocks')?.click(); setTimeout(() => [...document.querySelectorAll('button')].find((b) => b.textContent === 'Fit')?.click(), 250); } } }),
      el('button', { id: 'ai-reject', type: 'button', textContent: 'Reject', onclick: async () => { if (!(await ask('Reject proposal', 'Reject this proposal? Nothing in the project changes, but the proposal is closed.', [{ label: 'Reject', value: true, primary: true }, { label: 'Keep', value: false }]))) return; post('/reject').then(() => box.close()).catch(say); } }),
      checking ? buildSteps(t.build) : el('span', { className: 'note', textContent: invalid ? 'Does not build: Accept anyway applies it as a starting point.' : t.build.skipRequested ? 'No build or render was run. Source errors and warnings were checked; accepting is still available.' : 'Accepting is one change: it can be undone.' }));
  }

  async function confirmAnyway(t) {
    return new Promise((resolve) => {
      const dlg = dialogBox('Accept anyway?', 'ai-anyway');
      dlg.body.append(el('div', { className: 'tx-warn' }, [el('b', { textContent: 'The project does not build with this proposal' }), el('p', { textContent: 'Accepting it puts the changes in your working copy, unsaved, and the song may have errors until you fix them. You can undo it.' }), el('ul', {}, [...t.diagnostics.slice(0, 8), ...t.build.failures.slice(0, 8)].map((d) => el('li', { textContent: d })))]));
      dlg.foot.append(el('button', { type: 'button', className: 'primary', textContent: 'Accept anyway', onclick: () => { dlg.box.close(); resolve(true); } }), el('button', { type: 'button', textContent: 'Cancel', onclick: () => dlg.box.close() }));
      dlg.box.addEventListener('close', () => resolve(false), { once: true });
    });
  }
  poll();
}

// ---- the settings ----

const RISK = ['read project files and the linked files a task needs', 'send that code and project context to the configured AI service', 'write incorrect or incomplete code', 'run commands, create, change, rename, move and (when its mode allows it) delete files in its temporary copy', 'run builds and tools, and use up the quota of the Codex account', 'meet credentials if you keep secrets in files it may read'];

function confirmRisk(unsafe) {
  return new Promise((resolve) => {
    const dlg = dialogBox(unsafe ? 'Unsafe / Auto-all: more autonomy' : 'Turn on an AI coding agent for this project?', 'ai-risk');
    const check = el('input', { id: 'ai-risk-check', type: 'checkbox', onchange: () => { go.disabled = !check.checked; } });
    const go = el('button', { id: 'ai-risk-go', type: 'button', className: 'primary', textContent: unsafe ? 'Allow Unsafe / Auto-all' : 'Enable', disabled: true, onclick: () => { dlg.box.close(); resolve(true); } });
    dlg.body.append(
      el('p', { className: 'tx-lead', textContent: unsafe ? 'In this mode Codex is configured never to ask: it runs whatever its sandbox allows, in its own temporary copy, and what the sandbox does not allow is refused. (Only if the server administrator has switched the sandbox off for this mode does it get more reach.)' : 'Codex is a powerful coding agent. Depending on the mode you choose it may inspect project data, run commands and modify files while preparing a proposal.' }),
      el('ul', {}, (unsafe ? ['run commands and change or delete files in its temporary copy without asking you', 'use up quota quickly, because nothing slows it down', 'if the server has removed the sandbox: also touch anything the server user can'] : RISK).map((r) => el('li', { textContent: r }))),
      el('p', { textContent: 'AI can make mistakes. AMPLE provides review, checks, undo and recovery where it can, but you remain responsible for accepting the resulting changes. Even Unsafe does not accept a proposal for you.' }),
      el('label', { className: 'tx-opt wide' }, [check, el('span', {}, [el('b', { textContent: unsafe ? 'I understand and accept the extra risks of Unsafe / Auto-all for this project.' : 'I understand and accept the risks of enabling an AI coding agent for this project.' })])]));
    dlg.foot.append(go, el('button', { type: 'button', textContent: 'Cancel', onclick: () => dlg.box.close() }));
    dlg.box.addEventListener('close', () => resolve(false), { once: true });
  });
}

export async function openAiSettings() {
  let st;
  try { st = await aiStatus(true, true); } catch (error) { say(error); return; }
  const { box, body, foot, head } = dialogBox('Assistant', 'ai-settings');
  const policy = st.policy;
  const state = { assistant: st.project.assistant, mode: st.project.mode };
  const view = el('div', {});
  body.append(view);
  foot.append(el('button', { type: 'button', textContent: 'Close', onclick: () => box.close() }), el('span', { className: 'note', textContent: 'The server says what may be chosen; the project chooses what it uses.' }));
  const reload = async (auth = false) => { st = await aiStatus(true, auth); state.assistant = st.project.assistant; state.mode = st.project.mode; draw(); };

  const save = async (change) => {
    const next = { ...state, ...change };
    const payload = { assistant: next.assistant, mode: next.mode };
    if (next.assistant !== 'none' && !st.project.acknowledged) { if (!(await confirmRisk(false))) { draw(); return; } payload.acknowledgeRisk = true; }
    if (next.assistant !== 'none' && next.mode === 'unsafe' && !st.project.unsafeAcknowledged) { if (!(await confirmRisk(true))) { draw(); return; } payload.acknowledgeUnsafe = true; }
    try { st = await api.post('/api/ai/settings', payload); status = st; await reload(next.assistant !== 'none'); toast('Assistant settings saved.'); } catch (error) { say(error); draw(); }
  };

  function draw() {
    head.querySelector('.ai-badge')?.remove();
    head.insertBefore(badge(st), head.querySelector('.sp'));
    const allowedA = (a) => st.assistantsAllowed.includes(a), allowedM = (m) => st.modesAllowed.includes(m);
    const order = ['manual', 'auto', 'unsafe'];
    view.replaceChildren(
      section('Status', el('div', { className: st.available ? 'tx-done' : st.project.assistant === 'none' ? 'tx-warn' : 'tx-error', id: 'ai-status' }, [
        el('b', { textContent: st.project.assistant === 'none' ? 'No assistant for this project' : `${st.identity} · ${st.modeLabel}` }),
        el('p', { textContent: st.codex.found ? `Codex ${st.codex.version}` : 'Codex is not installed.' }),
        st.auth ? el('p', { textContent: st.auth.state === 'linked' ? `Signed in: ${st.auth.detail}` : `Not signed in: ${st.auth.detail}` }) : el('span'),
        ...st.problems.map((p) => el('p', { className: 'tx-hint', textContent: p }))])),
      section('Assistant for this project',
        opt('radio', 'ai-assistant', 'none', 'No assistant', 'Nothing is sent anywhere.', state.assistant === 'none', false, () => save({ assistant: 'none' })),
        opt('radio', 'ai-assistant', 'codex-server', 'Codex — Server account', allowedA('codex-server') ? 'Uses the Codex login of the user the server runs as, with its configuration.' : 'The server does not allow this.', state.assistant === 'codex-server', !allowedA('codex-server'), () => save({ assistant: 'codex-server' })),
        opt('radio', 'ai-assistant', 'codex-device', 'Codex — Project Device ID', allowedA('codex-device') ? 'A Codex identity of its own for this project, linked with the device flow. Separate from the Codex of the user.' : 'The server does not allow this.', state.assistant === 'codex-device', !allowedA('codex-device'), () => save({ assistant: 'codex-device' }))),
      section('Codex execution mode (what Codex may do while it works)',
        ...order.map((m) => opt('radio', 'ai-mode', m, { manual: 'Manual', auto: 'Auto', unsafe: 'Unsafe / Auto-all' }[m], { manual: 'Codex reads freely and asks before it writes or changes anything.', auto: 'Codex decides what is routine; it asks for what is destructive, privileged or unusual. (The default.)', unsafe: 'Codex never asks: whatever its sandbox (its own temporary copy) allows runs at once, and what the sandbox does not allow is refused. It still cannot accept a proposal into the project for you.' }[m] + (allowedM(m) ? '' : ' The server does not allow this.'), state.mode === m, !allowedM(m), () => save({ mode: m })))),
      el('p', { className: 'tx-hint', textContent: 'Less autonomy applies at once. More autonomy is a choice you make explicitly here; the first time for Unsafe needs its own acknowledgement. Codex never switches modes by itself.' }));
    if (state.assistant === 'codex-device') view.append(deviceSection());
    view.append(section('What this means', el('ul', {}, st.limitations.map((l) => el('li', { textContent: l })))), policySection());
  }

  function deviceSection() {
    const box2 = el('div', { id: 'ai-device' });
    const linked = st.project.linkedAccount;
    const start = async () => {
      try {
        const d = await api.post('/api/ai/device/start', {});
        box2.replaceChildren(el('div', { className: 'tx-warn' }, [el('b', { textContent: 'Link the Codex account of this project' }), el('p', {}, [document.createTextNode('Open '), el('a', { href: d.verificationUrl, target: '_blank', rel: 'noopener', textContent: d.verificationUrl, id: 'ai-device-url' }), document.createTextNode(' and enter this code:')]), el('div', { className: 'ai-code', id: 'ai-device-code', textContent: d.userCode }), el('p', { className: 'tx-hint', id: 'ai-device-state', textContent: 'Waiting for you to sign in...' })]));
        const wait = async () => { const s2 = await api.get('/api/ai/device/status'); if (s2.state === 'waiting') { setTimeout(wait, 2000); return; } if (s2.state === 'linked') { toast('Codex account linked to this project.'); await reload(true); } else { box2.querySelector('#ai-device-state').textContent = `Not linked: ${s2.error || s2.state}`; } };
        setTimeout(wait, 2000);
      } catch (error) { say(error); }
    };
    box2.append(el('p', { textContent: linked ? `Linked: ${linked}` : 'Not linked yet.' }), el('div', { className: 'tx-buttons' }, [
      el('button', { id: 'ai-link', type: 'button', className: 'primary', textContent: linked ? 'Re-authenticate' : 'Link Codex account', onclick: start }),
      linked ? el('button', { id: 'ai-unlink', type: 'button', textContent: 'Unlink project Codex', title: 'Removes the login of this project; the Codex of the user is not touched', onclick: async () => { try { await api.post('/api/ai/unlink', {}); await reload(true); } catch (error) { say(error); } } }) : el('span')]),
    el('p', { className: 'tx-hint', textContent: 'The login of this project is kept in a Codex home of its own on this machine, outside the project: it is never part of the song, a package or Git.' }));
    return section('Codex account of this project', box2);
  }

  function policySection() {
    const toggles = [['codexEnabled', 'Codex integration enabled'], ['allowServerAccount', 'Allow Codex using the server-user account'], ['allowDeviceId', 'Allow project-isolated Device ID authentication'], ['allowInheritance', 'Allow system-user Codex config / AGENTS.md inheritance'], ['allowManual', 'Allow Manual execution mode'], ['allowAuto', 'Allow Auto execution mode'], ['allowUnsafe', 'Allow Unsafe / Auto-all execution mode'], ['unsafeNoSandbox', 'Unsafe / Auto-all also removes the sandbox (not recommended: Codex can then touch anything this server user can)']];
    const modelBox = el('div', { className: 'ai-model', id: 'ai-model' }, [el('span', { className: 'tx-hint', textContent: 'Reading the models of Codex...' })]);
    api.get('/api/ai/models').then((r) => {
      const current = policy.model || r.default || 'gpt-6-luna';
      const known = r.models || [];
      const options = [...known.map((m) => [m.id, (m.name || m.id) + (m.id === r.default ? ' (AMPLE default)' : '')]), ...(known.some((m) => m.id === current) || current === 'default' ? [] : [[current, current]]), ['default', 'Let Codex choose its own default']];
      const modelSelect = el('select', { id: 'ai-model-select', 'aria-label': 'Model', onchange: async (e) => { try { await api.post('/api/ai/policy', { model: e.target.value }); await reload(true); } catch (error) { say(error); } } }, options.map(([v, l]) => el('option', { value: v, textContent: l })));
      modelSelect.value = options.some(([v]) => v === current) ? current : 'default';
      const efforts = ['default', 'low', 'medium', 'high', 'xhigh', 'max', 'ultra'];
      const effortSelect = el('select', { id: 'ai-effort-select', 'aria-label': 'Reasoning effort', onchange: async (e) => { try { await api.post('/api/ai/policy', { effort: e.target.value }); await reload(true); } catch (error) { say(error); } } }, efforts.map((v) => el('option', { value: v, textContent: v === 'default' ? 'the default of the model' : v })));
      effortSelect.value = policy.effort || 'default';
      const picked = known.find((m) => m.id === modelSelect.value);
      modelBox.replaceChildren(
        el('label', { className: 'tx-opt' }, [el('span', {}, [el('b', { textContent: 'Model' }), el('small', { textContent: picked?.description || 'The model Codex uses for the tasks of this server. A fast and affordable model is the default; a project does not choose it.' })]), modelSelect]),
        el('label', { className: 'tx-opt' }, [el('span', {}, [el('b', { textContent: 'Reasoning effort' }), el('small', { textContent: 'How much Codex thinks before it answers: more is slower and uses more of your Codex usage.' })]), effortSelect]),
        r.error ? el('p', { className: 'tx-hint', textContent: r.error }) : el('span'));
    }).catch(() => modelBox.replaceChildren(el('p', { className: 'tx-hint', textContent: 'The list of models could not be read.' })));
    return el('details', { className: 'ai-policy', id: 'ai-policy' }, [el('summary', { textContent: 'Server capabilities (what projects on this server may choose)' }), modelBox,
      el('p', { className: 'tx-hint', textContent: 'These switches only say what a project MAY select. They do not choose anything for a project, and when one is turned off nothing falls back to another identity or mode. A project can turn a switch off here, but turning it on again (or choosing the Codex program) is done by the administrator of this machine in the policy file, so that the page of a project cannot widen what it may do.' }),
      ...toggles.map(([key, label]) => el('label', { className: 'tx-opt' }, [el('input', { type: 'checkbox', checked: !!policy[key], disabled: !policy[key], title: policy[key] ? '' : 'Allowing more is done by the administrator in the policy file of this machine', 'data-policy': key, onchange: async (e) => { try { await api.post('/api/ai/policy', { [key]: e.target.checked }); await reload(true); } catch (error) { say(error); } } }), el('span', {}, [el('b', { textContent: label })])]))]);
  }
  draw();
}

document.addEventListener('ample:open-ai-settings', () => openAiSettings());
