// Koda's backend on the server: a Codex task of the kind "koda" (the pet lane: its own login when linked, one thing at a time, a short profile and a low effort). A question comes back as an answer, an instruction as a draft
// made of the changes of the task; Yes applies them through the same accept as the assistant, one undo step. @ample:koda
import { api } from './api.js';
import { requestParts } from './koda-model.js';
import { S, applyText, flat, reloadDoc } from './store.js';
import { KodaBusy, KodaUnavailable } from './koda-backend.js';
import { promptOf, splitAnswer, splitCheck, splitLong, splitTitle, subjectName, summaryOf, withoutNoChange } from '../lib/koda-context.mjs';
import { addedBlocks } from '../lib/koda-draft.mjs';
import { describeChanges } from '../lib/koda-diff.mjs';
import { glanced } from '../lib/koda-glance.mjs';
import { variantDrafts } from '../lib/koda-variants.mjs';
import { saveRecoveryDraft } from '../lib/recovery-drafts.mjs';
import { buildErrorsText } from '../lib/build-errors.mjs';
import { kodaAttachments } from './attach.js';

const wait = (ms, signal) => new Promise((resolve, reject) => {
  const timer = setTimeout(resolve, ms);
  signal?.addEventListener('abort', () => { clearTimeout(timer); reject(new DOMException('aborted', 'AbortError')); }, { once: true });
});

const kodaFailure = (error) => {
  const message = String(error?.message ?? error);
  const code = String(error?.code || message.match(/AMP\d+/)?.[0] || '');
  if (code === 'AMP5464') return new KodaBusy();
  if (/^AMP545[0-3]$/.test(code)) return new KodaUnavailable(`Koda uses the assistant of this project, and it is not ready: ${message.replace(/^AMP\d+:\s*/, '')}`);
  return error;
};

// what the user is looking at: the module when one is open, else the song (its text as it is in the editor, saved or not)
const inModule = () => S.moduleStack.length > 0 && !!S.modulePath;
const activePath = () => (inModule() ? S.modulePath : S.path);
export const activeText = () => (inModule() ? S.moduleDoc : S.doc)?.text;

let sessionId = '';
async function session() {
  if (!sessionId) sessionId = (await api.post('/api/ai/sessions', { title: 'Koda', archiveConversation: false })).id;
  return sessionId;
}

// "Hang around": the task Koda was last busy with; the next question can go on in its Codex conversation
let approver = null;
let lastTask = '', hanging = '', hangSince = 0, hooked = false, interrupted = false;   // hooked: the person chose this conversation in the detailed view, so the next question always goes on in it
const HANG_QUICK_MS = 5 * 60 * 1000;   // a next question within this time goes on without asking

/** The open tabs and what changed in the ones with unsaved changes: Koda works on the unsaved text and knows the difference. */
/** The blocks that failed in the last build, with the line they are blamed on when it is known: the build status of the blocks first (always there), the marks of the code as the extra. */
function failedBlocks() {
  const out = {};
  for (const [name, v] of Object.entries(S.build?.status || {})) {
    if (String(v?.status ?? '').toLowerCase() === 'failed') out[name] = { message: v.message, text: S.buildErrors?.items?.[name]?.text || '' };
  }
  for (const [name, item] of Object.entries(S.buildErrors?.items || {})) if (!out[name]) out[name] = item;
  return out;
}
async function tabsContext(subject) { try { return await (await import('./tabs.js')).tabsContextForKoda(subject); } catch { return ''; } }

// Bare metal mode (the third state of the draft / live switch): live, and the model is told to be quick and rough: assumptions instead of questions, a quick compile-style check, no waiting for a full build or render.
let bare = false;
export const setBare = (on) => { bare = !!on; };

// Bare metal also shows what the model makes while it works: after every edit it has made (seen by polling the proposed text of the task) the song in the editor is brought to
// that text, and what the person changes meanwhile is told to the model ("the user changed this: adapt and carry on"). The editor side (koda.js) provides show / start / end.
let liveHooks = null, liveTask = null, lastLive = '', liveBroken = false, userLines = [];
export const setLiveHooks = (hooks) => { liveHooks = hooks; };
async function liveStep(task) {
  if (!bare || !liveHooks || liveBroken || liveTask !== task.id || !activePath()) return;
  let text;
  try { text = (await api.get(`/api/ai/tasks/${task.id}/text?path=${encodeURIComponent(activePath())}`)).text; } catch { return; }
  if (typeof text !== 'string' || text === lastLive || text === activeText()) return;
  // the person edited meanwhile and the model's copy does not hold that edit (yet): never overwrite what the person wrote
  if (userLines.length && !userLines.every((l) => text.includes(l))) { liveBroken = true; liveHooks.paused?.(); return; }
  lastLive = text;
  await liveHooks.show(text);
}
/** The person changed the song while Koda works in bare metal mode: the proposal is brought up to date where it can be, and the model is told what changed. */
export async function tellUserEdit(prev, next) {
  if (!bare || !liveTask) return;
  const had = new Set(String(prev).split('\n').map((l) => l.trim()));
  const added = String(next).split('\n').map((l) => l.trim()).filter((l) => l && !had.has(l));
  userLines = [...userLines, ...added].slice(-200);
  const changes = describeChanges(prev, next).map((c) => c.type + ': ' + c.text).join('; ').slice(0, 600);
  try { await api.post(`/api/ai/tasks/${liveTask}/rebase`, {}); } catch { /* it may not apply: the live view is then paused if the model's copy lacks the edit */ }
  const lines = added.slice(0, 40).join('\n');
  await api.post(`/api/ai/tasks/${liveTask}/message`, { text: 'Attention: the user changed the song while you were working' + (changes ? ' (' + changes + ')' : '') + '. Their change stays: do not undo it. Adapt your plan to it and carry on with the rest of the task.' + (lines ? '\n\nNew or changed lines of the user:\n' + lines : '') }).catch(() => {});
}
const BARE_TEXT = '\n\nBARE METAL mode (the person chose it for speed and knows it is rough): work fast on what you are asked and do all of it in one go (for example five effects at once). Make sensible assumptions instead of asking, and say in one short line which assumptions you made. Check only what is quick: that the song text would compile (the diagnostics tool answers in seconds). Do not run or wait for a full build, a render or a long computation, and do not re-check what you already know. If something is unclear, take the likeliest choice and carry on.';

async function runTask(subject, text, mode, signal, onGlance, onThought, resume = false) {
  // the server needs the file Koda looks at (path is required): with no song and no module open there is nothing to look at, so say so instead of failing with "The field 'path' is required"
  if (!activePath()) throw new KodaUnavailable('Open a song first: Koda works on the song that is open in the editor.');
  let task;
  try {
    task = await api.post('/api/ai/tasks', {
      sessionId: await session(), kind: 'koda', scope: subject.kind === 'code' ? 'selection' : 'file', prompt: promptOf(subject, text, mode) + (bare ? BARE_TEXT : '') + buildErrorsText(failedBlocks()) + await tabsContext(subject), path: activePath(), song: S.path, context: 'unsaved',
      fromLine: subject.kind === 'code' ? subject.startLine : undefined, toLine: subject.kind === 'code' ? subject.endLine : undefined, block: subject.kind === 'code' ? subject.block?.name : subject.block,
      continueFrom: resume && hanging ? hanging : undefined,
      attachments: kodaAttachments().ids(),   // the pictures and files attached to this message (a screenshot of notes ...)
      ...requestParts(),   // the model and thinking level of this Koda session (nothing: the setting of the assistant)
    });
    lastTask = task.id;
    kodaAttachments().take();   // they went with the task
    if (bare && liveHooks) { liveTask = task.id; lastLive = ''; liveBroken = false; userLines = []; liveHooks.start?.(); }
  } catch (error) { throw kodaFailure(error); }
  // every finished answer keeps the conversation hanging by itself: the next question within HANG_QUICK_MS goes on without a choice or a question
  return follow(task, signal, onGlance, onThought).then((result) => { hanging = task.id; hangSince = Date.now(); return result; }).finally(() => { if (liveTask === task.id) { liveTask = null; liveHooks?.end?.(); } });
}

/** Waits until the task has finished its turn (a new one, or one that goes on after a message), showing what it reads and thinks on the way. */
async function follow(task, signal, onGlance, onThought, afterMessage = false) {
  // an abort normally cancels the task; a stop of the person (interrupt) only ends the turn, the conversation stays
  const stop = () => { if (interrupted) { interrupted = false; return Promise.resolve(); } return api.post(`/api/ai/tasks/${task.id}/cancel`, {}).catch(() => {}); };
  signal?.addEventListener('abort', stop, { once: true });
  let seq = 0, thought = '', shown = '';
  // right after a message the task can still say 'ready' for a moment: that is the old turn, not the answer
  let busySeen = !afterMessage, waited = 0;
  for (;;) {
    await wait(1200, signal).catch((e) => { throw e; });
    task = await api.get(`/api/ai/tasks/${task.id}?since=${seq}&details=1`);   // only the new log entries, with the arguments of the tool calls
    await liveStep(task).catch(() => {});   // bare metal: what the model has made so far goes into the song
    // the blocks the model is reading, for the thin lines that glance at them
    try {
      const g = glanced(task.log, flat().nodes.map((n) => n.id), seq, task.thought && task.thought !== thought ? task.thought : '');
      seq = g.seq; thought = task.thought || thought;
      if (g.blocks.length) onGlance?.(g.blocks);
      // the line of the model's own thinking; a tool timing (an older server says "Batch: 3 calls in 5 ms") is no thought
      if (task.thought && task.thought !== shown && !/^Batch: \d+ calls/.test(task.thought)) { shown = task.thought; onThought?.(shown); }
    } catch { /* decoration only */ }
    if (task.status !== 'ready') busySeen = true;
    if (task.status === 'ready' && (busySeen || ++waited > 8)) {
      // 'ready' has to hold for a moment: the model's turn can end, and AMPLE then checks the result and may hand it back for one more turn; only the last one has the changes
      await wait(1500, signal);
      const again = await api.get('/api/ai/tasks/' + task.id + '?details=1');
      if (again.status === 'ready') return again;
      task = again;
      continue;
    }
    if (task.status === 'failed') throw new Error(task.error || 'The task failed.');
    if (task.status === 'cancelled') throw new DOMException('aborted', 'AbortError');
    if (task.status === 'waiting-approval') {
      // the model asks for a permission: the bubble asks the user (live mode allows reading an asset by itself); without an answer it is declined, and the model goes on without
      if (!approver || !task.approvals?.length) { await stop(); throw new Error('Koda needs a permission that cannot be given here. Ask the assistant instead.'); }
      for (const a of task.approvals) {
        let decision = 'decline';
        try { decision = (await approver(a)) || 'decline'; } catch { /* declined */ }
        await api.post('/api/ai/tasks/' + task.id + '/answer', { approval: a.id, decision }).catch(() => {});
      }
    }
  }
}

const answerOf = (task) => (task.answer?.text ? { title: task.answer.title || '', text: task.answer.text, more: task.answer.more || '', action: task.answer.action || '' } : titled(task.summary));
const titled = (summary) => { const t = splitTitle(withoutNoChange(splitCheck(String(summary || '').trim()).text)); const [text, more] = splitLong(...splitAnswer(t.text)); return { title: t.title, text, more }; };
const first = (text) => { const t = withoutNoChange(splitCheck(String(text || '').trim()).text); const cut = t.search(/\n\s*\n/); return cut > 0 ? [t.slice(0, cut), t.slice(cut).trim()] : [t, '']; };

/**
 * Puts the proposed text of each file in the browser as unsaved text of that file (the same recovery draft the source editor keeps): nothing is written to the project, the file opens with the
 * text marked unsaved, and Save all keeps it or closing the tab throws it away. A file that does not exist yet is made empty first (so it can be opened), its text is the draft.
 * Returns the paths, or null when one of them could not be taken over (then the normal Accept is used).
 */
async function keepAsUnsaved(task, changes) {
  const paths = [];
  try {
    const files = await api.get('/api/project/files');
    for (const c of changes) {
      const text = (await api.get(`/api/ai/tasks/${task.id}/text?path=${encodeURIComponent(c.path)}`)).text;
      if (typeof text !== 'string') return null;
      let base = '';
      if (files.some((f) => f.path === c.path)) base = (await api.get(`/api/file?path=${encodeURIComponent(c.path)}`)).text ?? '';
      else await api.put('/api/file', { path: c.path, text: '' });
      saveRecoveryDraft(localStorage, S.project?.id || '', c.path, text, base, Date.now());
      await api.put('/api/wc', { path: c.path, text });   // the working copy too: the tab shows the unsaved mark, a build reads it
      paths.push(c.path);
    }
  } catch { return null; }
  return paths;
}

async function resultOf(task, subject, instruction, fromQuestion = false) {
  if (!task.changes?.length) { const a = answerOf(task); return { kind: 'answer', title: a.title || subjectName(subject), text: a.text || 'Koda had nothing to add.', more: a.more }; }
  // the proposed text of the song, so the editor can show the suggestion in the text itself
  let proposed = '';
  if (task.changes.some((c) => c.path === activePath())) {
    try { proposed = (await api.get(`/api/ai/tasks/${task.id}/text?path=${encodeURIComponent(activePath())}`)).text || ''; } catch { proposed = ''; }
  }
  // the model offered alternatives (ample_offer_variants): the one proposal is cut into a draft per variant, and Use this one puts only that one in the song
  if (task.variants?.length >= 2 && proposed && activeText()) {
    const base = activeText();
    const reject = () => api.post('/api/ai/tasks/' + task.id + '/reject', {}).catch(() => {});
    const checked = splitCheck(task.summary).check === 'ok';
    return {
      kind: 'variants', shared: true, say: first(task.summary)[0] || '', block: subject.block?.name ?? subject.block, text: instruction, discard: reject,
      drafts: variantDrafts(base, proposed, task.variants).map((d) => ({
        kind: 'draft', proposed: d.text, baseText: base, summary: d.summary, blocks: [], checked, taskId: task.id, name: d.name, listen: d.listen, note: d.note ? '**' + d.name + '**: ' + d.note : '**' + d.name + '**',
        async apply() { await reject(); await applyText(d.text); await reloadDoc(); return true; },
        async applyPartial(text) { await reject(); await applyText(text); await reloadDoc(); return true; },
        discard() { reject(); },
      })),
    };
  }
  return {
    proposed, baseText: activeText(), kind: 'draft', summary: proposed && activeText() ? (describeChanges(activeText(), proposed).length ? describeChanges(activeText(), proposed) : summaryOf(task.changes)) : summaryOf(task.changes), blocks: task.changes.flatMap((c) => addedBlocks(c.diff)), note: task.answer?.text || first(task.summary)[0] || undefined, checked: splitCheck(task.summary).check === 'ok', issue: splitCheck(task.summary).check === 'issue',
    taskId: task.id, fromQuestion, asked: instruction, more: task.answer?.more || '', diff: task.changes.map((c) => c.diff || '').join(String.fromCharCode(10)).trim(),
    async applyPartial(text) {
      // only some of the changes are kept: the proposal is put aside and the chosen text goes into the song as one undo step
      await api.post('/api/ai/tasks/' + task.id + '/reject', {}).catch(() => {});
      await applyText(text);
      await reloadDoc();
      return true;
    },
    async apply() {
      // the other files Koda changed or made (a score, a component, a note) are not written: each comes into the editor as unsaved text and opens in a tab, so the person looks at it and saves it (Save all) or reverts it
      const others = task.changes.filter((c) => c.path && c.path !== activePath() && !c.deleted);
      if (others.length && !others.some((c) => /\.amplet$/i.test(c.path))) {
        const unsaved = await keepAsUnsaved(task, others);
        if (unsaved) {
          if (proposed && task.changes.some((c) => c.path === activePath())) await applyText(proposed);
          await api.post('/api/ai/tasks/' + task.id + '/reject', {}).catch(() => {});   // the server writes nothing: what Koda made lives in the editor until it is saved
          await reloadDoc();
          // a tab for each file, with the unsaved mark; nothing is opened, so the person stays where they were (the song)
          for (const path of unsaved) document.dispatchEvent(new CustomEvent('ample:add-tab', { detail: path }));
          return true;
        }
      }
      let r;
      try { r = await api.post(`/api/ai/tasks/${task.id}/accept`, {}); } catch (error) {
        // bare metal: the song in the editor already holds what the model made (the live view), so the project no longer matches the base of the proposal and Accept can refuse:
        // the proposed text is then put in the song itself and the proposal is put aside (files other than the song are not carried along in this case)
        if (!lastLive || !proposed) throw error;
        await applyText(proposed);
        await api.post('/api/ai/tasks/' + task.id + '/reject', {}).catch(() => {});
        await reloadDoc();
        return true;
      }
      if (r.songText != null && S.path === r.song) await applyText(r.songText);
      await reloadDoc();
      return true;
    },
    discard() { api.post(`/api/ai/tasks/${task.id}/reject`, {}).catch(() => {}); },
  };
}

export const serverBackend = {
  setApprover(fn) { approver = fn; },
  hanging() { return !!hanging; },
  /** The model or thinking level changed while Koda works: the conversation uses it from its next turn. */
  async applyModel(model, effort) { if (lastTask) await api.post(`/api/ai/tasks/${lastTask}/assistant`, { model: model || '', effort: effort || '' }).catch(() => {}); },
  /** Extra input for the task that is working now (it steers it). */
  async steer(text) { if (!lastTask) throw new Error('Koda is not working on anything.'); await api.post(`/api/ai/tasks/${lastTask}/message`, { text, attachments: kodaAttachments().take() }); },
  /** The person stops the turn that works: the conversation is kept and the next question goes on in it. */
  async interrupt() { if (!lastTask) return; interrupted = true; hanging = lastTask; hangSince = Date.now(); hooked = true; await api.post(`/api/ai/tasks/${lastTask}/interrupt`, {}).catch(() => { interrupted = false; }); },
  hangQuick() { return !!hanging && (hooked || Date.now() - hangSince < HANG_QUICK_MS); },
  /** Koda goes on in the conversation of this task (from the detailed view of the assistant). */
  hook(taskId) { lastTask = hanging = taskId; hangSince = Date.now(); hooked = true; },
  letGo() { hanging = ''; hooked = false; },
  async explain(subject, question, { signal, onGlance, onThought, resume } = {}) {
    const task = await runTask(subject, question, 'explain', signal, onGlance, onThought, resume);
    // the model changed the song although this was a question: never lose that silently (it would say 'I extended it' while nothing is in the song), it is offered as a proposal
    if (task.changes?.length) return resultOf(task, subject, question, true);
    const a = answerOf(task);
    return { kind: 'answer', title: a.title || subjectName(subject), text: a.text || 'Koda had nothing to add.', more: a.more, taskId: task.id };
  },
  async run(subject, instruction, { signal, onGlance, onThought, resume } = {}) {
    return resultOf(await runTask(subject, instruction, 'run', signal, onGlance, onThought, resume), subject, instruction);
  },
  /** A request on top of the proposal that is on the table (the same working copy, nothing accepted yet): a question gets an answer, a change gives a new proposal (new variants when the model offers them again). */
  async continue(taskId, subject, text, { signal, onGlance, onThought } = {}) {
    let task;
    try { task = await api.post(`/api/ai/tasks/${taskId}/message`, { text }); } catch (error) { throw kodaFailure(error); }
    return resultOf(await follow(task, signal, onGlance, onThought, true), subject, text);
  },
};
