// What Koda is asked about, reduced to what is needed (a block, a piece of code, a field), and the answer that AMPLE can give by itself from the documentation of the templates. Pure: no DOM. @ample:koda

/** The documentation of a parameter in the template (the server calls it documentation, the specs doc). */
const docOf = (meta) => meta?.documentation ?? meta?.doc ?? '';
const clip = (text, max) => (String(text ?? '').length > max ? `${String(text).slice(0, max)}...` : String(text ?? ''));

/** A block: its type, what the type says about itself, its parameters with their documentation, and what it reads from and feeds. */
export function blockSubject(node, flat, templates) {
  const t = templates?.[node.tpl];
  const params = Object.entries(node.params ?? {}).map(([name, value]) => ({ name, value: clip(value, 200), doc: clip(docOf(t?.parameters?.find((p) => p.name === name)), 300) }));
  const links = flat?.links ?? [];
  return {
    kind: 'block', block: node.id, type: node.tpl, title: t?.title ?? node.tpl, description: t?.description ?? '',
    params, reads: links.filter((l) => l.to === node.id).map((l) => l.from), feeds: links.filter((l) => l.from === node.id).map((l) => l.to),
    disabled: !!node.flags?.includes('disabled'),
  };
}

/** A group of blocks on the canvas (a box the user made around some blocks): its name and the blocks in it with their types. */
export const groupSubject = ({ id, title = '', members = [] }) => ({ kind: 'group', id: String(id ?? ''), title: String(title || id || ''), members: members.slice(0, 40).map((m) => ({ block: String(m.block ?? ''), type: String(m.type ?? '') })), count: members.length });

/** A part of the sequencer or of AMPLE Jam (a button, a channel, a lane, a tool, the ruler...): which window, its name, what its tooltip says, its value and the thing it sits in. */
export const partSubject = ({ area = '', block = '', pattern = '', label = '', hint = '', value = '', context = '' }) => ({ kind: 'part', area: String(area), block: String(block), pattern: String(pattern), label: clip(label, 80), hint: clip(hint, 300), value: clip(value, 120), context: clip(context, 80) });

/** A field of a block: its name, value and everything the template says about it. */
export function fieldSubject(node, name, templates) {
  const t = templates?.[node.tpl];
  const meta = t?.parameters?.find((p) => p.name === name) ?? {};
  return {
    kind: 'field', block: node.id, type: node.tpl, blockTitle: t?.title ?? node.tpl, param: name, label: meta.label ?? name, value: clip(node.params?.[name] ?? '', 300),
    doc: docOf(meta), defaultValue: meta.default ?? '', options: meta.options ?? null, min: meta.min ?? null, max: meta.max ?? null, unit: meta.unit ?? '', fieldKind: meta.kind ?? meta.type ?? 'text',
  };
}

/** A block type of the library (what can be added): its description and its parameters with their documentation. */
export function typeSubject(template) {
  const params = (template?.parameters ?? []).slice(0, 12).map((p) => ({ name: p.name, doc: clip(docOf(p), 200) }));
  return { kind: 'type', type: template?.id ?? '', title: template?.title ?? template?.id ?? '', description: template?.description ?? '', params };
}

/** What Koda knows about the blocks that make a control (a number over time that steers a parameter). */
const CONTROL_BLOCKS = {
  follow: 'A follow block listens to a sound and makes a control that is high while the sound is loud: band says which part is followed, baseline subtracts the steady background (rain), attack and release say how fast the control moves, auto lets AMPLE choose them from the recording (ample follow-suggest prints what it chose).',
  curve: 'A curve block makes a control from a curve that is written like a parameter (auto, lfo, env), so one curve can steer several parameters.',
  map: 'A map block rescales a control, can turn it around, limit and smooth it, and gives it a unit (Hz, dB) so it can steer a parameter in that unit.',
  fire: 'A fire block plays a note every time a control passes a level, with a minimum time between two notes (for example one hit for every car).',
};

/** A template card (a beat or a template of the project): its name and what it says about itself. */
export const templateSubject = ({ name, say = '', meta = '', project = false }) => ({ kind: 'template', name: clip(name, 120), say: clip(say, 400), meta: clip(meta, 200), project });

/** A port of a block (an input or an output): its name and stream type, the friendly title and description of its template, and (for a region) the effects it has passed. */
export const portSubject = ({ block, dir = 'out', name = '', type = '', title = '', description = '', chain = '' }) => ({ kind: 'port', block: String(block ?? ''), dir: dir === 'in' ? 'in' : 'out', name: String(name ?? ''), type: String(type ?? ''), title: clip(title, 80), description: clip(description, 400), chain: clip(chain, 200) });

/** A file or folder of the project. */
export const fileSubject = (path, isDir = false) => ({ kind: 'file', path: String(path ?? ''), isDir: !!isDir });

/** A revision of the history (a dot of the branch graph): its id, message, line, Redux reference and time. */
export const revisionSubject = ({ id, message = '', line = '', ref = '', at = '' }) => ({ kind: 'revision', id: String(id ?? ''), message: clip(message, 200), line: String(line ?? ''), ref: String(ref ?? ''), at: String(at ?? '') });

/** A branch of the project (a line of the history): its name, and whether it is the one the user is on or the main branch. */
export const branchSubject = ({ name, current = false, studio = false }) => ({ kind: 'branch', name: String(name ?? ''), current: !!current, studio: !!studio });

/** A part of a block as it is placed in the arrangement (a bar on a track): the block, its first bar and the number of bars. */
export const barSubject = ({ block, at, length, name = '' }) => ({ kind: 'bar', block: String(block ?? ''), at: Number(at) || 1, length: Number(length) || 1, name: String(name ?? '') });

/** The whole song (the name at the top left of the canvas): everything in it. */
export const songSubject = ({ path = '', name = '' } = {}) => ({ kind: 'song', path: String(path ?? ''), name: String(name ?? '') });

/** The arrangement itself (the timeline of the song), optionally the bar the user points at. */
export const arrangementSubject = ({ bar = 0 } = {}) => ({ kind: 'arrangement', bar: Number(bar) || 0 });

import { HEADER_TAIL } from './block-header.mjs';

const HEADER = new RegExp(String.raw`^\s*([A-Za-z][\w-]*)\s+([A-Za-z][\w-]*)` + HEADER_TAIL);

/** A piece of code: the selected text, the lines it covers, and the block it sits in (found by walking up to the header line of that block). */
export function codeSubject(text, from, to) {
  const all = String(text ?? '').split('\n');
  const before = String(text ?? '').slice(0, from);
  const startLine = before.split('\n').length - 1;
  const endLine = startLine + String(text ?? '').slice(from, to).split('\n').length - 1;
  let block = null;
  for (let i = startLine; i >= 0; i--) {
    const m = HEADER.exec(all[i]);
    if (m) { block = { type: m[1], name: m[2], line: i + 1 }; break; }
  }
  return { kind: 'code', text: clip(String(text ?? '').slice(from, to), 2000), startLine: startLine + 1, endLine: endLine + 1, block };
}

/** The name of the subject for the bubble ("piano", "the field humanize of piano", "lines 12 to 14"). */
export function subjectName(s) {
  if (s.kind === 'multi') return s.items.length > 3 ? `${s.items.slice(0, 3).map(subjectName).join(', ')} and ${s.items.length - 3} more` : s.items.map(subjectName).join(' and ');
  if (s.kind === 'block') return s.block;
  if (s.kind === 'part') return `${s.label}${s.context ? ' of ' + s.context : ''} (${s.area}${s.block ? ' of ' + s.block : ''})`;
  if (s.kind === 'group') return `the group ${s.title}`;
  if (s.kind === 'field') return `${s.param} of ${s.block}`;
  if (s.kind === 'type') return `the ${s.title} block`;
  if (s.kind === 'template') return s.name;
  if (s.kind === 'port') return `the ${s.title || s.name} ${s.dir === 'in' ? 'input' : 'output'} of ${s.block}`;
  if (s.kind === 'file') return String(s.path).split('/').filter(Boolean).pop() || s.path;
  if (s.kind === 'revision') return `the revision ${s.ref || s.id}`;
  if (s.kind === 'branch') return `the branch ${s.name}`;
  if (s.kind === 'bar') return `${s.block} at bar ${s.at}`;
  if (s.kind === 'song') return s.name ? `the song ${s.name}` : 'the song';
  if (s.kind === 'arrangement') return s.bar ? `the arrangement at bar ${s.bar}` : 'the arrangement';
  return s.startLine === s.endLine ? `line ${s.startLine}` : `lines ${s.startLine} to ${s.endLine}`;
}

/**
 * What AMPLE can tell by itself, without a model: { title, text, more } (more is a longer text for the details), or null when the subject needs a real answer (code without a known block).
 * Koda says this first; "tell me more" and "do this" go to the model.
 */
export function explainLocally(s, templates) {
  if (s.kind === 'field') {
    const parts = [];
    if (s.doc) parts.push(s.doc);
    const facts = [];
    if (s.value !== '') facts.push(`It is ${s.value} now.`);
    if (s.defaultValue !== '' && String(s.defaultValue) !== String(s.value)) facts.push(`The usual value is ${s.defaultValue}.`);
    if (s.options?.length) facts.push(`You can choose: ${s.options.join(', ')}.`);
    if (s.min !== null && s.max !== null) facts.push(`It goes from ${s.min} to ${s.max}${s.unit ? ' ' + s.unit : ''}.`);
    return { title: `${s.label}`, text: parts[0] || `${s.label} is a setting of ${s.blockTitle}, and its documentation does not say more.`, more: facts.join(' ') };
  }
  if (s.kind === 'block') {
    const link = (names, word) => (names.length ? `${word} ${names.join(', ')}.` : '');
    const lines = [s.description, link(s.reads, 'It reads from'), link(s.feeds, 'It feeds'), s.disabled ? 'It is disabled now, so it makes no sound.' : ''].filter(Boolean);
    return { title: `${s.block} is a ${s.title}`, text: lines.join(' '), more: s.params.filter((p) => p.doc).slice(0, 6).map((p) => `${p.name} (${p.value || 'empty'}): ${p.doc}`).join('\n') };
  }
  if (s.kind === 'group') return { title: `${s.title} is a group of ${s.count} block${s.count === 1 ? '' : 's'}`, text: s.members.map((m) => `${m.block} (${m.type})`).join(', ') + '.', more: '' };
  if (s.kind === 'part') return { title: s.label, text: s.hint || `${s.label} is a part of ${s.area}${s.context ? ', of ' + s.context : ''}.`, more: s.value ? `It is ${s.value} now.` : '' };
  if (s.kind === 'type') return { title: `${s.title} (block type)`, text: s.description || `A ${s.type} block.`, more: s.params.filter((p) => p.doc).slice(0, 8).map((p) => `${p.name}: ${p.doc}`).join('\n') };
  if (s.kind === 'template') return { title: s.name, text: s.say || 'A template that can be added to the song.', more: s.meta };
  if (s.kind === 'code' && s.block) {
    const t = templates?.[s.block.type];
    return { title: `This is in ${s.block.name}`, text: `${t?.description ?? `A ${s.block.type} block.`} The selection is ${subjectName(s)}.`, more: '' };
  }
  return null;
}

/** Several things the user linked Koda to at once (Ctrl+Shift and drag): the list of their subjects. */
export const multiSubject = (items) => ({ kind: 'multi', items });

/** The lines that say what the user is looking at. */
function describe(subject) {
  const lines = [];
  if (subject.kind === 'multi') {
    lines.push(`The user is looking at ${subject.items.length} things together, and the request is about how they relate:`);
    subject.items.forEach((item, i) => lines.push(`${i + 1}. ${describe(item).join(' ')}`));
    return lines;
  }
  if (subject.kind === 'block') {
    lines.push(`The user is looking at the block ${subject.block} (a ${subject.type}).`);
    if (CONTROL_BLOCKS[subject.type]) lines.push(CONTROL_BLOCKS[subject.type]);
  }
  else if (subject.kind === 'group') lines.push(`The user is looking at the group ${subject.title}, a box around ${subject.count} block${subject.count === 1 ? '' : 's'}: ${subject.members.map((m) => `${m.block} (a ${m.type})`).join(', ')}.`);
  else if (subject.kind === 'part') lines.push(`The user is looking at "${subject.label}" in ${subject.area}${subject.pattern ? `, which shows the pattern "${subject.pattern}" of the sequencer block ${subject.block || 'of the song'} (these notes are part of the sequence text of that sequencer block, not of a midi-clip). Change only that channel in that pattern; leave every other channel, pattern and clip of the sequence exactly as it is` : subject.block ? ' of the block ' + subject.block + ' (the notes of that block are its notes parameter in the song)' : ''}${subject.context ? ' (' + subject.context + ')' : ''}${subject.value ? ', now: ' + subject.value : ''}${subject.hint ? '. Its tooltip says: ' + subject.hint : ''}.`);
  else if (subject.kind === 'field') lines.push(`The user is looking at the field ${subject.param} (now: ${subject.value || 'empty'}) of the block ${subject.block} (a ${subject.type}).`);
  else if (subject.kind === 'type') lines.push(`The user is looking at the block type ${subject.title} (${subject.type}) in the block library.`);
  else if (subject.kind === 'template') lines.push(`The user is looking at the ${subject.project ? 'project ' : ''}template "${subject.name}"${subject.say ? `: ${subject.say}` : ''}.`);
  else if (subject.kind === 'revision') lines.push(`The user points at one revision (a saved point) of the project history${subject.ref ? `, ${subject.ref}` : ''} (id ${subject.id}), message "${subject.message}", on the branch "${subject.line}"${subject.at ? `, made ${subject.at}` : ''}. The branch name is not a block of the song. To see what that revision changed use ample_revision with id=${subject.id} (add path=<file> for a file's diff); ample_history is only the list of earlier AI requests.`);
  else if (subject.kind === 'branch') lines.push(`The user is looking at the branch ${subject.name}${subject.current ? ' (the one they are on)' : ''}${subject.studio ? ' (the main branch)' : ''}. ample_history can tell more about it.`);
  else if (subject.kind === 'bar') lines.push(`The user is looking at the part of the block ${subject.block} that is placed in the arrangement at bar ${subject.at} for ${subject.length} bars${subject.name ? ` (${subject.name})` : ''}. In the song it is the \`at ${subject.at} for ${subject.length}\` of that block.`);
  else if (subject.kind === 'song') lines.push(`The user is looking at the whole song${subject.name ? ` ${subject.name}` : ''}${subject.path ? ` (file ${subject.path})` : ''}: every block in it and how they work together, not one block. Read the song (ample_song) before answering.`);
  else if (subject.kind === 'arrangement') lines.push(`The user is looking at the arrangement (the timeline of the song with the bars of every block)${subject.bar ? `, around bar ${subject.bar}` : ''}.`);
  else if (subject.kind === 'port') lines.push(`The user is looking at the ${subject.dir === 'in' ? 'input' : 'output'} port "${subject.name}"${subject.title ? ` (shown as "${subject.title}")` : ''} of the block ${subject.block}; it carries ${subject.type}${subject.description ? `: ${subject.description}` : ''}${subject.chain ? ` Along the way it passes ${subject.chain}.` : ''}${subject.type === 'region' ? ' A region is a part of the timeline (the joins of a loop): effects can be chained on it, it never connects to an audio input, and only a region-merge block turns it back into audio.' : ''}`);
  else if (subject.kind === 'file') lines.push(`The user is looking at the ${subject.isDir ? 'folder' : 'file'} ${subject.path} of the project.`);
  else lines.push(`The user selected ${subjectName(subject)}${subject.block ? `, inside the block ${subject.block.name}` : ''}:`, subject.text);
  return lines;
}

/**
 * The short answer and the part to unfold, from what the model wrote: the short answer is what comes before the first blank line or before the first line that starts structure
 * (a bullet, a numbered item, a heading, a table row, a line that is only **bold**); everything from there on is the part to unfold. Returns [text, more].
 */
export function splitAnswer(text) {
  const lines = String(text ?? '').trim().split('\n');
  const structure = (l) => /^\s*(?:[-*\u2022]\s+|\d+[.)]\s+|#{1,4}\s|\|.*\|\s*$|\*\*[^*]+\*\*:?\s*$)/.test(l);
  let cut = lines.findIndex((l, i) => i > 0 && (!l.trim() || structure(l)));
  if (cut < 0) cut = lines.length;
  return [lines.slice(0, cut).join('\n').trim(), lines.slice(cut).join('\n').trim()];
}

/**
 * A long answer that came as one block (the model did not keep to a short answer and the details): the first sentences stay the answer, the rest is the part that unfolds.
 * Short answers, and answers that already have a part to unfold, are left alone. Returns [text, more].
 */
export function splitLong(text, more = '') {
  const t = String(text ?? '').trim();
  if (more || t.length <= 260) return [t, more];
  const sentences = t.split(/(?<=[.!?])\s+(?=[A-Z`"'(])/);
  if (sentences.length < 3) return [t, more];
  let keep = 0, length = 0;
  while (keep < sentences.length - 1 && keep < 3 && (keep < 1 || length + sentences[keep].length <= 300)) { length += sentences[keep].length + 1; keep++; }
  return [sentences.slice(0, keep).join(' '), sentences.slice(keep).join(' ')];
}

/** How a question wants to be answered: a title, a short answer, and (only when there is much to say) details that fold away. */
export const ANSWER_FORM = 'Deliver your answer with the ample_answer tool, and do not repeat it in your final message. Its fields: title (3 to 6 words), answer (at most three sentences), and details: the part the user can unfold, not raw data but the IMPACT, in markdown: one bullet per block, setting or element (its name in `code`, then what it does to the sound and to the song and why that matters here, exact values only where they explain that impact), and a final heading "Summary" with a more detailed account of how it all works together. A small markdown table is fine in the details when facts line up in columns. Never one big block of text. Give details with almost every answer about blocks, settings or the song; leave them out only when the answer is trivial. (If you cannot use the tool: write a first line "Title: ...", then the short answer, then a blank line and the details.)';

/** Added when the question is about several things together: there is always something to unfold then. */
export const ANSWER_FORM_MANY = ' This question is about several things at once, so the part to unfold is always there: one bullet per thing about its impact here (look it up first, with ample_song or ample_block, so what you say is exact), then a heading "Summary" with a more detailed account of how they work together.';

/** The title the model gave (a first line "Title: ..." or "# ..."), and the rest of its answer. */
export function splitTitle(text) {
  const t = String(text ?? '').trim();
  const m = /^(?:#{1,3}\s*|title:\s*)(.+?)\s*(?:\n|$)/i.exec(t);
  if (!m) return { title: '', text: t };
  return { title: m[1].replace(/^\*\*|\*\*$/g, '').trim().slice(0, 80), text: t.slice(m[0].length).trim() };
}

/** What Koda is told: the subject in a few lines, and the question or the instruction. */
export function promptOf(subject, text, mode) {
  const lines = describe(subject);
  if (subject.said) lines.push(`You just told the user this about it (they may ask a follow-up question, or ask you to do what you suggested):\n${subject.said}`);
  if (mode !== 'run') lines.push(subject.kind === 'multi' ? ANSWER_FORM + ANSWER_FORM_MANY : ANSWER_FORM);
  lines.push(mode === 'run' ? `Instruction: ${text}` : `${text ? `Question: ${text}` : 'Question: what does this do? Answer briefly.'} This answer changes nothing in the song (do not say that nothing was changed). If the text is not a question but asks for a change ("mix it up", "make it louder"), do not refuse and do not ask whether the user wants it: say in the answer what you would do, and put that one sentence in the action field of ample_answer, so the user can press Do that.`);
  return lines.join('\n');
}

/**
 * What the model says about its own check (ample_check is a tool, not something the user knows): the sentences about it are taken out of the text. check is 'ok' when it said there is nothing new
 * to report (Koda then shows a small tick), 'issue' when it reported something (that sentence stays, in plain words), null when the check was not mentioned.
 */
export function splitCheck(text) {
  const parts = String(text ?? '').split(/(?<=[.!?])\s+/);
  const keep = [];
  let check = null;
  for (const part of parts) {
    if (!/ample_check|AMPLE check/i.test(part)) { keep.push(part); continue; }
    if (/\bno (?:new )?(?:errors?|warnings?|problems?|issues?)\b|\bclean\b|\bpasses\b|\bwithout (?:new )?(?:errors?|warnings?)\b/i.test(part) && !/\b(?:found|reports?|has|have)\s+(?:\d+|some|new|several)\s+(?:new\s+)?(?:errors?|warnings?)/i.test(part)) { check ??= 'ok'; continue; }
    check = 'issue';
    keep.push(part.replace(/`?ample_check`?/gi, 'the AMPLE check'));
  }
  return { text: keep.join(' ').trim(), check };
}

/** The answer of the model without the closing remark that nothing was changed (it is a question: that goes without saying). */
const NO_CHANGE = /(^|[.!?]\s+)\(?\bno (?:changes?|files?|edits?)(?: were| was| have been| has been)?(?: made| changed| needed)?\b\)?[.!]?\s*$/i;
export const withoutNoChange = (text) => String(text ?? '').replace(NO_CHANGE, '$1').trim();

/** Where the documentation says something about a subject: the page of its block type in the help (a block, a field, a block type, code inside a block), else the language page. */
export function docsHref(s) {
  const type = s?.kind === 'code' ? s.block?.type : s?.type;
  if (type && ['block', 'field', 'type', 'code'].includes(s.kind)) return `/help#template-${String(type).toLowerCase().replace(/[^a-z0-9]+/g, '-')}`;
  if (s?.kind === 'code') return '/help#language-03-syntax';
  return '/help';
}

/** A short list of the changes of a task from its unified diffs: the lines that were added or taken out. */
export function summaryOf(changes) {
  const items = [];
  for (const change of changes) {
    const added = [], removed = [];
    for (const line of String(change.diff || '').split('\n')) {
      if (line.startsWith('+++') || line.startsWith('---')) continue;
      if (line.startsWith('+') && line.slice(1).trim()) added.push(line.slice(1).trim());
      else if (line.startsWith('-') && line.slice(1).trim()) removed.push(line.slice(1).trim());
    }
    for (const text of added.slice(0, 4)) items.push({ type: /->|^from /.test(text) ? 'link' : 'add', text });
    if (added.length > 4) items.push({ type: 'add', text: `and ${added.length - 4} more lines` });
    if (removed.length) items.push({ type: 'remove', text: `${removed.length} line${removed.length === 1 ? '' : 's'} taken out` });
  }
  return items;
}
