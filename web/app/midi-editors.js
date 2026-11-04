// Editable MIDI note text views. Source remains canonical; comments travel with the expanded notes body.
import { $, ask, el, toast } from './ui.js';
import { S, applyText, current, notifySelection, readOnly } from './store.js';
import { compactMidiNotes, expandMidiNotes, isCompactMidiNotes } from '../lib/expanded-midi-notes.js';
import { api } from './api.js';
import { playTransient } from './player.js';
import { previewMidiNotes } from '../lib/gm-audition.js';
import { midiEventKinds, midiEventLine, midiEventRecords, midiEventSummary, pageMidiEvents, pairMidiNoteEvents, tieGroups, validMidiEvent } from '../lib/midi-events.js';

function unquote(value) { return value.startsWith('"') && value.endsWith('"') ? value.slice(1, -1).replace(/\\(["\\nt])/g, (_, c) => ({ n: '\n', t: '\t' }[c] || c)) : value; }
function head(line) {
  const match = /^\s*midi-clip\s+("(?:[^"\\]|\\.)*"|[^\s]+)\s+.*\{\s*$/.exec(line);
  return match ? unquote(match[1]) : null;
}

function locate(source, name, lineHint = -1) {
  const lines = source.replace(/\r\n/g, '\n').split('\n');
  let first = lineHint >= 0 && head(lines[lineHint] || '') === name ? lineHint : lines.findIndex((line) => head(line) === name);
  if (first < 0) return null;
  let depth = 1, close = -1, ppq = 1_000_000, notes = null;
  for (let i = first + 1; i < lines.length; i++) {
    const line = lines[i].trim();
    if (depth === 1) {
      const scale = /^ppq\s+(\d+)$/.exec(line);
      if (scale) ppq = Number(scale[1]);
      if (line.startsWith('notes ')) {
        if (line === 'notes {') {
          let nested = 1, end = i;
          for (let j = i + 1; j < lines.length; j++) {
            const item = lines[j].trim();
            if (item.endsWith('{')) nested++;
            else if (item === '}') nested--;
            if (!nested) { end = j; break; }
          }
          if (nested) return null;
          notes = { start: i, end, expanded: true, value: lines.slice(i, end + 1).map((x) => x.trimStart().replace(/^  /, '')).join('\n') };
          i = end;
        } else notes = { start: i, end: i, expanded: false, value: line.slice(6).trim().replace(/^"|"$/g, '') };
      }
    }
    if (line.endsWith('{')) { depth++; if (line === 'notes {' && notes?.expanded) depth--; }
    else if (line === '}' && --depth === 0) { close = i; break; }
  }
  if (close < 0) return null;
  if (!notes) notes = { start: close, end: close - 1, expanded: false, value: '' };
  return { lines, first, close, ppq, ...notes };
}

function withIndent(expanded) { return expanded.split('\n').map((line) => `  ${line}`).join('\n'); }
function replaceNotes(source, info, expanded) {
  const lines = [...info.lines];
  const start = info.start, end = info.end;
  const block = withIndent(expanded).split('\n');
  lines.splice(start, end < start ? 0 : end - start + 1, ...block);
  return lines.join('\n');
}

async function editDialog(source, info, onSave) {
  let initial = info.value;
  if (!info.expanded) {
    initial = expandMidiNotes(info.value, info.ppq);
    if (initial === null) { toast('This notes line is invalid. Fix it in the source editor before opening the notes editor.'); return false; }
  }
  const editor = el('textarea', { value: initial, spellcheck: false, wrap: 'off', className: 'notes-editor', 'aria-label': 'MIDI notes as named fields', style: 'width:100%;box-sizing:border-box;height:min(72vh,760px);min-height:200px;resize:both;font:13px/1.45 ui-monospace,Consolas,monospace;white-space:pre;tab-size:2' });
  while (true) {
    const save = await ask('Edit MIDI notes', 'Named fields use the same timing as the compact notes line. # comments stay in the source beside their notes.', [{ label: 'Cancel', value: false }, { label: 'Save notes', value: true, primary: true }], [editor]);
    if (!save) return false;
    if (compactMidiNotes(editor.value, info.ppq) === null) { toast('Notes are invalid. Check required fields, tick ranges and bar limits, then open the editor again.'); return false; }
    return onSave(replaceNotes(source, info, editor.value));
  }
}

export async function openMidiNotesForBlock(node) {
  if (!node || node.tpl !== 'midi-clip' || readOnly()) return;
  const inModule = !!(S.moduleStack.length && S.modulePath);
  const source = inModule ? S.moduleDoc?.text : S.doc?.text;
  if (!source) return;
  const info = locate(source, node.id, node.line);
  if (!info) { toast('Could not find this clip’s notes in the current source.'); return; }
  await editDialog(source, info, async (next) => !!(await applyText(next)));
}

function moduleAtLine(source, lineIndex) {
  const lines = source.split('\n');
  for (let i = lineIndex; i >= 0; i--) {
    const name = head(lines[i]);
    if (name) return { name, line: i };
  }
  return null;
}

export function hasValidInlineNotesAt(source, lineIndex) {
  const lines = source.split('\n');
  if (!/^\s*notes\s+/.test(lines[lineIndex] || '') || lines[lineIndex].trim() === 'notes {') return false;
  const block = moduleAtLine(source, lineIndex);
  if (!block) return false;
  const info = locate(source, block.name, block.line);
  return !!info && !info.expanded && info.start === lineIndex && isCompactMidiNotes(info.value, info.ppq);
}

export async function openMidiNotesAtLine(lineIndex, getText, setText) {
  const source = getText();
  const block = moduleAtLine(source, lineIndex);
  if (!block) return false;
  const info = locate(source, block.name, block.line);
  if (!info || info.start !== lineIndex || info.expanded || !isCompactMidiNotes(info.value, info.ppq)) return false;
  return editDialog(source, info, async (next) => { setText(next); return true; });
}

async function editMidiEvent(event, save) {
  const editor = el('textarea', { value: JSON.stringify({ tick: event.tick, kind: event.kind, channel: event.channel, data1: event.data1, data2: event.data2, meta: event.meta, payload: event.payload }, null, 2), spellcheck: false, wrap: 'off', style: 'width:min(760px,76vw);height:min(55vh,600px);font:13px/1.45 ui-monospace,Consolas,monospace' });
  if (!await ask('Edit MIDI event', 'Use tick, kind, channel, data1, data2, meta and hexadecimal payload. Note On/Off events are rebuilt from the clip notes.', [{ label: 'Cancel', value: false }, { label: 'Apply event', value: true, primary: true }], [editor])) return;
  try {
    const parsed = JSON.parse(editor.value), next = { ...parsed, track: event.track, line: event.line };
    if (!validMidiEvent(next)) throw new Error();
    await save(next);
  } catch { toast('Invalid event. Check integer ranges, event kind and hexadecimal payload.'); }
}
function showMidiEvents(node) {
  const doc = S.moduleStack.length ? S.moduleDoc : S.doc;
  if (!doc) return;
  const track = Number(node.params.track), events = midiEventRecords(doc.text, track), box = $('#props');
  if (!box) return;
  const props = el('div', { className: 'pad' });
  box.replaceChildren(props);
  if (node.back) props.append(el('button', { className: 'mini', textContent: '← Back to the block', onclick: node.back }));
  const clipBlock = doc.blocks.find((item) => item.name === node.params.clip);
  const ties = tieGroups(clipBlock?.parameters?.find((q) => q.name === 'notes')?.value);
  if (!events.length && !ties.length) { toast('No retained MIDI events or note ties found for this track.'); return; }
  const tieSection = ties.length ? el('div', { className: 'midi-ties' }, [
    el('h4', { textContent: `Note ties · ${ties.length}` }),
    el('p', { className: 'hint', textContent: 'A note that runs over a bar line is stored as parts that share a tie-id. Each tie is listed with its pitch and bars; open the clip notes to change them (the tie-id field stays with its parts).' }),
    ...ties.map((tie) => el('div', { className: 'midi-event-row' }, [
      el('div', { textContent: `tie ${tie.id} · pitch ${tie.pitch} · bars ${tie.bars.join(' → ')} · ${tie.parts} parts` }),
      el('button', { textContent: 'Open clip notes', onclick: () => { if (clipBlock) openMidiNotesForBlock({ id: clipBlock.name, line: clipBlock.line, tpl: clipBlock.type }); } }),
    ])),
  ]) : null;
  if (!events.length) { props.append(el('h4', { textContent: node.title }), tieSection); return; }
  const paired = pairMidiNoteEvents(events);
  props.append(el('h4', { textContent: node.title }), el('p', { className: 'hint', textContent: 'Events stay in the module source and are exported in tick order. Matched note events open the clip editor; unmatched note events can be inspected and repaired here.' }));
  if (tieSection) props.append(tieSection);
  const filter = el('select', { 'aria-label': 'Filter MIDI event kind' }, [el('option', { value: '', textContent: 'All event kinds' }), ...midiEventKinds.map((kind) => el('option', { value: kind, textContent: kind }))]);
  const search = el('input', { type: 'search', placeholder: 'Tick or channel…', 'aria-label': 'Filter by MIDI tick or channel' });
  const count = el('span', { className: 'hint' }), list = el('div', { className: 'midi-event-list' });
  let page = 0;
  const paint = () => {
    const query = search.value.trim().toLowerCase();
    const visible = pageMidiEvents(events, filter.value, query, page, 50);
    page = visible.page; count.textContent = `${visible.total} events · ${page + 1}/${visible.pages}`;
    list.replaceChildren(...visible.events.map((event) => {
      const note = event.kind === 'NoteOn' || event.kind === 'NoteOff', linked = paired.has(event.line), summary = midiEventSummary(event);
      return el('div', { className: 'midi-event-row' }, [
        el('div', { textContent: `#${event.line + 1} · tick ${event.tick} · ${event.kind} · channel ${event.channel + 1} · ${summary}${note ? linked ? ` · paired with line ${paired.get(event.line) + 1}` : ' · unmatched' : ''}` }),
        el('div', { className: 'row' }, [
          el('button', { textContent: linked ? 'Open clip notes' : 'Edit event', disabled: readOnly(), onclick: () => {
            if (note && linked) { const clip = doc.blocks.find((item) => item.name === node.params.clip); if (clip) openMidiNotesForBlock({ id: clip.name, line: clip.line, tpl: clip.type }); return; }
            editMidiEvent(event, async (next) => {
              const lines = doc.text.replace(/\r\n/g, '\n').split('\n');
              if (note && pairMidiNoteEvents(midiEventRecords(lines.map((line, index) => index === event.line ? midiEventLine(next) : line).join('\n'), track)).has(event.line)) { toast('Keep this note event unmatched; matched notes are edited in the clip editor.'); return false; }
              lines[next.line] = midiEventLine(next); return applyText(lines.join('\n'));
            });
          } }),
          el('button', { textContent: 'Remove', disabled: readOnly() || (note && linked), onclick: async () => {
            if (!await ask('Remove MIDI event?', `This removes the retained ${event.kind} event from the module source and MIDI export.`, [{ label: 'Cancel', value: false }, { label: 'Remove event', value: true }])) return;
            const lines = doc.text.replace(/\r\n/g, '\n').split('\n'); lines.splice(event.line, 1); await applyText(lines.join('\n'));
          } }),
        ]),
      ]);
    }));
  };
  filter.onchange = search.oninput = () => { page = 0; paint(); };
  const add = el('button', { textContent: 'Add event', disabled: readOnly(), onclick: () => editMidiEvent({ track, line: events.at(-1).line + 1, tick: 0, kind: 'ControlChange', channel: 0, data1: 7, data2: 100, meta: 'none', payload: '' }, async (next) => { const lines = doc.text.replace(/\r\n/g, '\n').split('\n'); lines.splice(events.at(-1).line + 1, 0, midiEventLine(next)); await applyText(lines.join('\n')); }) });
  props.append(el('div', { className: 'row' }, [filter, search, el('button', { textContent: '‹', title: 'Previous page', onclick: () => { page--; paint(); } }), count, el('button', { textContent: '›', title: 'Next page', onclick: () => { page++; paint(); } })]), list, add);
  paint();
}

function previewNoteValues(block) {
  const source = block?.parameters?.find((parameter) => parameter.name === 'notes')?.value?.trim().replace(/^"|"$/g, '') || '';
  const ppqText = block?.parameters?.find((parameter) => parameter.name === 'ppq')?.value;
  const ppq = ppqText ? Number(ppqText) : 1_000_000;
  return previewMidiNotes(source, ppq, 128);
}
function showGmAudition(node) {
  $('#props [role="tab"][data-key="parameters"]')?.click();
  const props = $('#props .prop-tabpanel.active'), doc = S.moduleStack.length ? S.moduleDoc : S.doc;
  if (!props || !doc) return;
  const block = doc.blocks.find((item) => item.name === node.id), input = block?.froms?.find((from) => from.input === 'notes'), clip = input && doc.blocks.find((item) => item.name === input.block);
  const excerpt = previewNoteValues(clip);
  const value = (name) => node.params[name] || '';
  const run = async (notes) => {
    try {
      // a value can carry a unit ("-7 dB") or be a curve: the number in front of it counts, never NaN (that travels as null and the server refuses it)
      const num = (name) => { const n = parseFloat(String(value(name)).replace(/^"|"$/g, '')); return Number.isFinite(n) ? n : 0; };
      const result = await api.gmAudition({ file: value('file'), bank: Math.trunc(num('bank')), program: Math.trunc(num('program')), channel: Math.trunc(num('channel')), gain: num('gain'), limiter: value('limiter') || 'soft', notes });
      if (result.warnings) toast(result.warnings);
      await playTransient(result.blob);
    } catch (error) { toast(error.message); }
  };
  const button = el('button', { textContent: 'Preview note', title: 'Audition current bank, program and channel', onclick: () => run([{ bar: 1, tick: 0, ticks: 480, ppq: 480, pitch: 60, velocity: 90 }]) });
  const track = el('button', { textContent: 'Preview connected track', disabled: !excerpt.length, onclick: () => run(excerpt) });
  const heading = el('h4', { textContent: 'Audition' });
  props.append(heading, el('div', { className: 'row midi-gm-audition' }, [button, track]));
}

export function renderMidiPropertyTools() {
  const selectedNode = S.sel && current().nodes.find((node) => node.id === S.sel);
  if (selectedNode?.isMidiEventNode) { showMidiEvents(selectedNode); return; }
  if (selectedNode?.tpl === 'gm') { showGmAudition(selectedNode); return; }
  const selected = S.sel && (S.moduleStack.length ? S.moduleDoc : S.doc)?.blocks?.find((block) => block.name === S.sel);
  if (!selected || selected.type !== 'midi-clip') return;
  $('#props [role="tab"][data-key="parameters"]')?.click();
  const props = $('#props .prop-tabpanel.active');
  if (!props) return;
  props.querySelectorAll('.prop').forEach((row) => { if (row.querySelector('label')?.textContent === 'notes') row.remove(); });
  const button = el('button', { textContent: '✎ Edit notes…', title: 'Open the note-by-note MIDI editor', disabled: readOnly(), onclick: () => openMidiNotesForBlock({ id: selected.name, line: selected.line, tpl: selected.type }) });
  const info = current().nodes.find((n) => n.id === selected.name)?.midiEvents;
  const events = info ? el('button', { textContent: `MIDI events · ${info.count}${info.ties ? ` · ${info.ties} ties` : ''}`, title: 'The retained MIDI events (tempo, controllers, pitch bends) and note ties of this track', onclick: () => showMidiEvents({ title: `${selected.name} events`, params: { track: String(info.track), count: String(info.count), ties: String(info.ties), clip: selected.name }, back: () => { notifySelection(); } }) }) : null;
  const row = el('div', { className: 'row midi-note-editor' }, [button]);
  const anchor = [...props.querySelectorAll('h4')].find((heading) => heading.textContent === 'Parameters');
  (anchor?.parentElement || props).insertBefore(row, anchor?.nextSibling || null);
  if (events) row.after(el('div', { className: 'row midi-event-open' }, [events]));
}
