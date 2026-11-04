// Koda's suggestion inside the source editor, like a ghost completion that spans lines: the lines that would go are tinted, the lines that would come are shown dashed under their place, and a small bar offers
// Accept (Tab), a short remark to talk it over (Enter) and Reject (Esc). The editor itself is not touched: this is an overlay on its wrapper, drawn from the text of the draft and the text of the editor. @ample:koda
import { el } from './ui.js';
import { structuredHunks, describeChanges } from '../lib/koda-diff.mjs';
import { tx } from './koda-lang.js';

let live = null;

export const inlineShown = () => !!live;

/** Whether the source editor of the song is open right now. */
export const editorOpen = () => !!document.querySelector('.sewrap') && !!document.querySelector('#se-ta') && !!document.querySelector('#vs-source.on');

/**
 * proposed: the text of the song after the draft; handlers { onAccept(), onReject(), onDiscuss(text), onStale() }. Returns { hide, update(proposed), busy(flag) } or null when there is nothing to show.
 */
export function showInline({ proposed, onAccept, onReject, onDiscuss, onStale }) {
  hideInline();
  const wrap = document.querySelector('.sewrap');
  const ta = document.querySelector('#se-ta');
  const hl = document.querySelector('#se-hl');
  if (!wrap || !ta || !hl) return null;
  let base = ta.value;
  let hunks = structuredHunks(base, proposed);
  if (!hunks.length) return null;

  const content = el('div', { className: 'ki-content' });
  const count = el('b', { className: 'ki-count' });
  const input = el('input', { className: 'ki-input', placeholder: tx('inline.placeholder'), 'aria-label': tx('inline.placeholder') });
  const accept = el('button', { type: 'button', className: 'ki-accept', textContent: tx('inline.accept'), title: tx('inline.acceptTitle'), onclick: (e) => { e.stopPropagation(); onAccept?.(); } });
  const reject = el('button', { type: 'button', className: 'ki-reject', textContent: tx('inline.reject'), title: tx('inline.rejectTitle'), onclick: (e) => { e.stopPropagation(); onReject?.(); } });
  const send = () => { const text = input.value.trim(); if (text) { input.value = ''; onDiscuss?.(text); } };
  input.onkeydown = (e) => { e.stopPropagation(); if (e.key === 'Enter') { e.preventDefault(); send(); } if (e.key === 'Escape') { e.preventDefault(); onReject?.(); } };
  const bar = el('div', { className: 'ki-bar', role: 'group', 'aria-label': tx('inline.group') }, [el('span', { className: 'ki-tag', textContent: 'Koda' }), count, accept, input, reject]);
  const layer = el('div', { id: 'koda-inline', className: 'koda-inline' }, [content, bar]);
  wrap.append(layer);

  const paint = () => {
    const css = getComputedStyle(hl);
    const lh = parseFloat(css.lineHeight) || 18;
    const pad = parseFloat(css.paddingTop) || 6;
    content.replaceChildren();
    content.style.font = css.font;
    // the width of a character of the editor, to put a replacement at the end of the line it replaces
    const probe = el('span', { style: 'position:absolute;visibility:hidden;white-space:pre', textContent: 'x'.repeat(50) });
    content.append(probe);
    const cw = probe.getBoundingClientRect().width / 50 || 7;
    probe.remove();
    const baseLines = base.split('\n');
    const padLeft = parseFloat(css.paddingLeft) || 8;
    for (const h of hunks) {
      if (h.removed) content.append(el('div', { className: 'ki-del', style: `top:${pad + h.start * lh}px;height:${h.removed * lh}px`, title: 'Koda would take these lines out' }));
      if (!h.added.length) continue;
      if (h.removed === 1 && h.added.length === 1) {
        // one line for another: the new line is a ghost at the end of the old one, nothing is covered
        const old = baseLines[h.start] ?? '';
        content.append(el('span', { className: 'ki-ghost', style: `top:${pad + h.start * lh}px;left:${padLeft + (old.length + 3) * cw}px;height:${lh}px;line-height:${lh}px`, title: 'Koda would put this line instead' }, ['→ ' + h.added[0].trim()]));
        continue;
      }
      content.append(el('pre', { className: 'ki-add', style: `top:${pad + (h.start + h.removed) * lh}px`, title: 'Koda would add these lines' }, [h.added.join('\n')]));
    }
    const a = describeChanges(base, proposed).length;
    count.textContent = `${a === 1 ? tx('inline.action1') : tx('inline.actions', { n: a })} · ${hunks.length === 1 ? tx('inline.change1') : tx('inline.changes', { n: hunks.length })}`;
  };
  paint();
  // bring the first change into view (a few lines of context above it)
  {
    const css = getComputedStyle(hl);
    const lh = parseFloat(css.lineHeight) || 18;
    const at = (parseFloat(css.paddingTop) || 6) + hunks[0].start * lh;
    if (at < ta.scrollTop || at + lh * 3 > ta.scrollTop + (ta.clientHeight || 300)) ta.scrollTop = Math.max(0, at - 4 * lh);
  }

  // the overlay follows the scrolling of the editor and notices when the text was edited meanwhile (then the suggestion no longer fits)
  let raf = 0, stale = false;
  const follow = () => {
    content.style.transform = `translateY(${-ta.scrollTop}px)`;
    if (ta.value !== base && !stale) { stale = true; hideInline(); onStale?.(); return; }
    raf = requestAnimationFrame(follow);
  };
  raf = requestAnimationFrame(follow);
  const keys = (e) => {
    if (e.key === 'Tab' && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey) { e.preventDefault(); e.stopPropagation(); onAccept?.(); }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); onReject?.(); }
  };
  wrap.addEventListener('keydown', keys, true);

  live = {
    hide() { cancelAnimationFrame(raf); wrap.removeEventListener('keydown', keys, true); layer.remove(); },
    update(next) { base = ta.value; proposed = next; hunks = structuredHunks(base, next); if (!hunks.length) { hideInline(); return; } paint(); },
    busy(flag) { layer.classList.toggle('ki-busy', !!flag); input.disabled = !!flag; accept.disabled = !!flag; if (!flag) input.focus(); },
  };
  return { hide: hideInline, update: live.update, busy: live.busy };
}

export function hideInline() { live?.hide(); live = null; }
