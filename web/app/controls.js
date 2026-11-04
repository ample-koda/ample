// Seeds and the freeze ladder of a block, with the explainer ("if I change X, what happens") from the server.
// Seeds are locked by default; unlocking says what it breaks. @ample:seeds @ample:controls

import { api } from './api.js';
import { S, edit, loadHistory, notify, readOnly } from './store.js';
import { ask, el, toast } from './ui.js';
import { openVariants } from './variants.js';
import { toggle, stop } from './player.js';

const FREEZE = [
  ['free', 'Free', 'Nothing is frozen: every build can come out differently.'],
  ['seeds', 'Seeds', 'Both seeds are fixed: the same take comes back only if the words, settings, tool and computer stay exactly the same.'],
  ['plan', 'Melody (plan)', 'The melody codes and the seeds are stored: the song keeps its melody and structure. You can re-sing it, restyle it or tweak the words at your own risk.'],
  ['take', 'Take', 'The rendered audio is stored (baked): final. Nothing changes until you unbake it.'],
];
const SEEDLBL = { lm_seed: 'Melody seed (lm_seed)', seed: 'Performance seed' };
const MAX_SEED = 18446744073709551615n;
const OUTCOME = { free: ['ok', 'free'], ignored: ['lock', 'ignored'], 'changes-the-result': ['dyn', 'changes the result'], 'breaks-reproduction': ['missing', 'breaks reproduction'] };
const enc = encodeURIComponent;
let token = 0;

/** What happens to the seeds at this freeze level (shown when unlocking and after a change). */
export function seedWarnText(level) {
  return level === 'take'
    ? 'A baked take is stored, so the seeds are ignored until you unbake it.'
    : level === 'plan'
      ? 'The melody is frozen: a new melody seed has no effect until you unfreeze; a new performance seed re-sings the same melody.'
      : 'The current take can no longer be reproduced from the new seed. It stays in the take bank and in the seed history; baking it keeps it for good.';
}

async function setLevel(n, level) {
  const baked = n.flags.includes('bake');
  const ops = [];
  if (level === 'take') { if (!baked) ops.push({ type: 'addFlag', block: n.id, flag: 'bake' }); } else {
    if (baked) ops.push({ type: 'removeFlag', block: n.id, flag: 'bake' });
    ops.push({ type: 'setFreeze', block: n.id, level });
  }
  if (ops.length && (await edit(ops))) toast(`"${n.title}" is now frozen as ${FREEZE.find((f) => f[0] === level)[1]}.`);
}

async function seedCall(n, body) {
  try { return await api.post('/api/control/seed', { path: S.path, block: n.id, ...body }); } catch (e) { toast(`${e.code ? `${e.code}: ` : ''}${e.message}`); return null; }
}

/** Adds the seeds and controls sections of a dynamic block to the properties panel. */
export async function renderControls(pad, n) {
  const holder = el('div');
  holder.dataset.controls = n.id;
  pad.append(holder);
  const mine = ++token;
  let info;
  try { info = await api.get(`/api/control/block?path=${enc(S.path)}&block=${enc(n.id)}`); } catch (e) { holder.append(el('p', { className: 'note', textContent: e.message })); return; }
  if (mine !== token) return;
  const ro = readOnly();
  const level = info.level;
  const unlocked = info.seeds.some((s) => !s.state.locked);

  const head = el('div', { className: 'row', style: 'margin:12px 0 4px' });
  head.append(el('h4', { textContent: 'Seeds', style: 'padding:0;margin:0' }), el('span', { className: `tag ${unlocked ? 'dyn' : 'ok'}`, textContent: unlocked ? '🔓 unlocked' : '🔒 locked' }), el('span', { className: 'tag', textContent: info.seeds.map((s) => s.state.origin).filter((v, i, a) => a.indexOf(v) === i).join(', ') }));
  holder.append(head);
  holder.append(el('p', { className: 'note', textContent: unlocked ? `Unlocked: you can edit. ${seedWarnText(level)}` : 'Locked: the seeds stay as they are, so the same take comes back (same tool and computer). Unlock to change them; the old ones are kept.' }));
  info.seeds.forEach((s) => {
    const row = el('div', { className: 'prop', style: 'grid-template-columns:140px 1fr' });
    const inp = el('input', { value: s.state.value, disabled: !unlocked || ro, inputMode: 'numeric' });
    inp.dataset.seed = s.name;
    inp.onchange = async () => {
      if (!/^\d{1,20}$/.test(inp.value.trim()) || BigInt(inp.value.trim()) > MAX_SEED) { toast('A seed is an unsigned 64-bit whole number.'); inp.value = s.state.value; return; }
      if (await seedCall(n, { name: s.name, action: 'set', value: inp.value.trim(), reason: 'typed in the UI' })) { toast(`${SEEDLBL[s.name] || s.name} changed. ${seedWarnText(level)}`); notify(); }
    };
    const box = el('div', { className: 'row', style: 'flex-wrap:nowrap;margin:0' }, [inp]);
    if (unlocked && !ro) box.append(el('button', { textContent: '🎲', title: 'A random seed', onclick: async () => { if (await seedCall(n, { name: s.name, action: 'randomize' })) { toast(`${SEEDLBL[s.name] || s.name} randomized. ${seedWarnText(level)}`); notify(); } } }));
    row.append(el('label', { textContent: SEEDLBL[s.name] || s.name }), box);
    holder.append(row);
  });
  const r = el('div', { className: 'row' });
  r.append(unlocked
    ? el('button', { textContent: 'Lock the seeds', disabled: ro, onclick: async () => { for (const s of info.seeds) await seedCall(n, { name: s.name, action: 'lock' }); notify(); } })
    : el('button', { textContent: 'Unlock to edit...', disabled: ro, onclick: async () => {
      const ok = await ask(`Unlock the seeds of "${n.title}"?`, `${seedWarnText(level)}\n\nThe old seeds are kept in the seed history, so you can always come back to them.`, [{ label: 'Cancel', value: false }, { label: 'Unlock', value: true, primary: true }]);
      if (ok) { for (const s of info.seeds) await seedCall(n, { name: s.name, action: 'unlock' }); notify(); }
    } }), el('button', { textContent: 'Try several...', title: 'Make several takes with different seeds and choose one', onclick: () => openVariants(n.id) }));
  holder.append(r);
  const past = info.seeds.flatMap((s) => s.history.map((h) => ({ ...h, seed: s.name }))).slice(-5).reverse();
  if (past.length) {
    holder.append(el('div', { className: 'note', textContent: 'Earlier seeds:', style: 'margin:6px 0 2px' }));
    past.forEach((h) => holder.append(el('div', { className: 'note', style: 'margin:1px 0;font-size:11px', textContent: `${SEEDLBL[h.seed] || h.seed}: ${h.oldValue} → ${h.newValue} (${h.reason})` })));
  }

  holder.append(el('h4', { textContent: 'Controls: what can I pin and tweak?', style: 'padding:0;margin:12px 0 4px' }));
  const sel = el('select', { disabled: ro });
  FREEZE.forEach(([k, l]) => sel.append(el('option', { value: k, textContent: l, selected: level === k })));
  sel.dataset.freeze = n.id;
  sel.onchange = () => setLevel(n, sel.value);
  holder.append(el('div', { className: 'prop' }, [el('label', { textContent: 'frozen as' }), sel]), el('p', { className: 'note', textContent: FREEZE.find((f) => f[0] === level)[2] }));
  info.explain.warnings.forEach((w) => holder.append(el('div', { className: 'seerr warning', textContent: `▲ ${w}` })));
  const tbl = el('div', { style: 'font-size:12px' });
  tbl.append(el('div', { className: 'ctlrow ctlhead', style: 'grid-template-columns:1fr 1fr' }, ['if I change...', 'what happens'].map((x) => el('span', { textContent: x }))));
  info.explain.effects.forEach((e) => {
    const [cls, label] = OUTCOME[e.outcome] || ['', e.outcome];
    tbl.append(el('div', { className: 'ctlrow', style: 'grid-template-columns:1fr 1fr' }, [el('span', { textContent: e.edit }), el('span', { className: `tag ${cls}`, textContent: label })]));
  });
  holder.append(tbl, el('p', { className: 'note', textContent: 'These are the rules the build applies at this freeze level, from the server.' }));
  const r2 = el('div', { className: 'row' });
  r2.append(
    el('button', { textContent: 'Freeze the melody now', disabled: ro, title: 'Store the melody codes and seeds of the last take', onclick: () => setLevel(n, 'plan') }),
    el('button', { textContent: 'Bake the take', disabled: ro, onclick: () => setLevel(n, 'take') }),
    el('button', { textContent: 'Re-sing...', title: 'Variants that keep the melody (performance seed only)', onclick: () => openVariants(n.id, 'resing') }),
    el('button', { textContent: 'Re-write...', title: 'Variants with new melodies', onclick: () => openVariants(n.id, 'rewrite') }),
    el('button', { textContent: 'Regenerate...', disabled: ro, title: 'Make the block again; if the result differs you decide what to keep', onclick: () => regenerate(n, info) }),
    el('button', { textContent: 'Check reproduction...', disabled: ro, title: 'Render with the reference seed and current software; compare with the saved take', onclick: (event) => verifyReproduction(n, info, event.currentTarget) }),
  );
  holder.append(r2);
  (info.pending || []).forEach((p) => holder.append(pendingRow(n, p)));
}

function pendingRow(n, p) {
  const row = el('div', { className: 'seerr warning', style: 'cursor:default' });
  row.dataset.pending = p.id;
  row.append(el('div', { textContent: `▲ A regeneration made a different take (${p.differences.join(', ') || 'content'}). The previous take is still the one in use.` }));
  const r = el('div', { className: 'row' });
  r.append(el('button', { textContent: 'Restore previous take', className: 'primary', onclick: () => decide(p.id, 'restore') }), el('button', { textContent: 'Keep the new take as a variant', onclick: () => decide(p.id, 'keep-variant') }));
  row.append(r);
  return row;
}

async function decide(id, decision) {
  try { await api.post('/api/control/decide', { path: S.path, id, decision }); toast(decision === 'restore' ? 'The previous take stays.' : 'The new take is kept as a variant.'); notify(); } catch (e) { toast(e.message); }
}

async function verifyReproduction(n, info, button) {
  const reference = info.selected || S.build.takes?.[n.id];
  if (!reference) { toast('Build this block first to obtain a reference take.'); return; }
  button.disabled = true;
  try {
    if (!info.selected) await api.post('/api/control/select', { path: S.path, block: n.id, take: reference });
    const result = await api.post('/api/control/verify-reproduction', { path: S.path, block: n.id });
    const players = el('div', { className: 'row' }, [el('button', { textContent: '▶ Reference take', onclick: () => toggle(result.oldTake).catch((e) => toast(e.message)) }), el('button', { textContent: '▶ New render', onclick: () => toggle(result.newTake).catch((e) => toast(e.message)) }), el('button', { textContent: '■ Stop', onclick: stop })]);
    await ask(result.status === 'same' ? 'Reproduction: identical bytes' : 'Reproduction: different result', [...(result.warnings || []), ...(result.differences || []), 'The reference take remains selected. Both takes are stored.'].join('\n\n'), [{ label: 'Close', value: true, primary: true }], [players]);
  } catch (error) { toast(error.message); }
  finally { button.disabled = false; notify(); }
}

async function regenerate(n, info) {
  const fromBuild = !info.selected && S.build.takes && S.build.takes[n.id];
  const extra = fromBuild ? '\n\nNo take is selected for this block yet: the take of your last build becomes the selected one.' : '';
  const ok = await ask(`Regenerate "${n.title}"?`, `${info.warning}\n\nThe take you have now is set aside and stays in use. If the new result differs you will be asked what to do, and "restore the previous take" is the default.${extra}`, [{ label: 'Cancel', value: false }, { label: 'Regenerate', value: true, primary: true }]);
  if (!ok) return;
  let r;
  try {
    if (fromBuild) await api.post('/api/control/select', { path: S.path, block: n.id, take: fromBuild });
    r = await api.post('/api/control/regenerate', { path: S.path, block: n.id }); } catch (e) { toast(e.message); return; }
  const res = r.result;
  if (res.status === 'same') { toast('Same result. Nothing changed.'); notify(); return; }
  const answer = await ask('The result is different', `${(res.differences || []).join('\n') || 'The new take differs from the previous one.'}\n\nWhat should happen? (Closing this window keeps the previous take; you can decide later.)`, [{ label: 'Restore the previous take (default)', value: 'restore', primary: true }, { label: 'Keep the new take as a variant', value: 'keep-variant' }]);
  if (answer) await decide(res.id, answer); else notify();
  await loadHistory();
}
