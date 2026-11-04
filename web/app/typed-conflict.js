// The song changed (blocks, undo, the assistant) while text was typed in the source editor and not applied yet. Applying the typed text as it is would drop the other changes,
// so this shows both sets of changes against the text that was loaded (a three-way view) and lets the person choose: merge both (when they do not touch the same lines),
// use the typed text, keep the song as it is now, or go back to the editor.
import { el } from './ui.js';
import { describeHunk, merge3, splitLines } from '../lib/three-way.mjs';

const MAX_HUNKS = 8;

function changes(title, note, base, lines, hunks) {
  const list = hunks.slice(0, MAX_HUNKS).map((hunk) => el('pre', { className: 'tc-hunk', textContent: `@ line ${hunk.a0 + 1}\n${describeHunk(base, lines, hunk).join('\n')}` }));
  if (hunks.length > MAX_HUNKS) list.push(el('p', { className: 'note', textContent: `... and ${hunks.length - MAX_HUNKS} more change${hunks.length - MAX_HUNKS === 1 ? '' : 's'}` }));
  if (!hunks.length) list.push(el('p', { className: 'note', textContent: 'No changes.' }));
  return el('section', { className: 'tc-col' }, [el('h3', { textContent: `${title} (${hunks.length})` }), el('p', { className: 'note', textContent: note }), ...list]);
}

/** @returns {Promise<{ choice: 'merge' | 'typed' | 'song' | 'cancel', text?: string }>} */
export function resolveTypedConflict(baseText, typedText, currentText) {
  const merged = merge3(baseText, typedText, currentText);
  const base = splitLines(baseText);
  return new Promise((resolve) => {
    const dialog = el('dialog', { className: 'typed-conflict-dialog', 'aria-label': 'The song changed while you were typing' });
    const done = (value) => { dialog.close(); dialog.remove(); resolve(value); };
    const status = merged.conflicts
      ? el('p', { className: 'tc-status bad', textContent: `${merged.conflicts} place${merged.conflicts === 1 ? '' : 's'} where both changed the same lines: they cannot be merged by themselves.` })
      : el('p', { className: 'tc-status good', textContent: 'The two sets of changes do not touch the same lines: both can be kept.' });
    const merge = el('button', { type: 'button', className: 'primary', textContent: 'Keep both', disabled: merged.conflicts > 0, title: 'Apply the typed text together with the changes made elsewhere', onclick: () => done({ choice: 'merge', text: merged.text }) });
    dialog.append(
      el('h2', { textContent: 'The song changed while you were typing' }),
      el('p', { textContent: 'Text typed in the editor is not applied yet, and the song was changed somewhere else in the meantime (the blocks, an undo or the assistant). Applying the typed text as it is would drop those changes.' }),
      el('div', { className: 'tc-cols' }, [
        changes('Your typed changes', 'Compared with the text the editor started from.', base, splitLines(typedText), merged.mine),
        changes('Changes made elsewhere', 'In the song since then.', base, splitLines(currentText), merged.theirs),
      ]),
      status,
      el('div', { className: 'tc-actions' }, [
        merge,
        el('button', { type: 'button', textContent: 'Use my typed text', title: 'Apply the typed text; the changes made elsewhere are dropped (Undo brings them back)', onclick: () => done({ choice: 'typed' }) }),
        el('button', { type: 'button', textContent: 'Keep the song as it is', title: 'Throw the typed text away; it stays available as a recovery draft', onclick: () => done({ choice: 'song' }) }),
        el('span', { className: 'sp' }),
        el('button', { type: 'button', textContent: 'Back to the editor', onclick: () => done({ choice: 'cancel' }) }),
      ]),
    );
    dialog.oncancel = (event) => { event.preventDefault(); done({ choice: 'cancel' }); };
    document.body.append(dialog);
    dialog.showModal();
    (merged.conflicts ? dialog.querySelector('.tc-actions button:nth-child(2)') : merge).focus();
  });
}
