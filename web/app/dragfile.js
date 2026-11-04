// Dragging a project file: what is being dragged is kept here, because the browser only lets a drop target read the data at the drop itself
// (not while it is dragged over), and the editors light up the places that fit while the drag goes on (plan project-panel PP04, PP05).
import { kindById, kindOf } from '../lib/file-kinds.mjs';
import { closeMenu } from './ui.js';

let current = null, endedAt = -10000;

/** Call from `dragstart` of a file or folder in the project panel or a file list. */
export function beginFileDrag(event, item) {
  const kind = kindOf(item.path, !!item.isDir);
  current = { path: item.path, isDir: !!item.isDir, kind };
  event.dataTransfer?.setData('application/x-ample-path', item.path);
  event.dataTransfer?.setData('text/plain', item.path);
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'copyMove';
  closeMenu();   // on a phone the long press that starts the drag may already have opened the menu of the item
  markFields(true);
}

/** Whether files from the computer are being dragged (while dragging the browser only says so in `types`; `files` is empty until the drop). */
export const dragHasFiles = (event) => [...(event.dataTransfer?.types || [])].includes('Files');

/** The project file that is being dragged now, or null. */
export const draggedFile = () => current;

/** Whether a file field (accepted extensions, mode file or folder) takes the file that is dragged. */
export function fieldAccepts(drag, accept, mode) {
  if (!drag) return false;
  if (mode === 'folder') return drag.isDir;
  if (drag.isDir) return false;
  return !accept?.length || accept.some((extension) => drag.path.toLowerCase().endsWith(extension.toLowerCase()));
}

/** Marks the file fields on the page that take the dragged file, so one sees where it can go. */
function markFields(on) {
  for (const input of document.querySelectorAll('.file-field input[data-mode]')) {
    const accept = (input.dataset.accept || '').split(',').filter(Boolean);
    input.classList.toggle('drop-ok', on && fieldAccepts(current, accept, input.dataset.mode));
  }
}

export const endFileDrag = () => { if (current) endedAt = performance.now(); current = null; markFields(false); };

// On a phone a long press starts the drag, and the same press also ends in a contextmenu event (when the file is dropped on itself or on a folder): that is not a request for the menu.
addEventListener('contextmenu', (event) => {
  if (current || performance.now() - endedAt < 900) { event.preventDefault(); event.stopImmediatePropagation(); }
}, true);

addEventListener('dragend', endFileDrag);
addEventListener('drop', () => setTimeout(endFileDrag, 0));

/** The kind of a file that comes from outside (the operating system), guessed from its type while it is dragged over; null when it is not a file or not known. */
export function outsideDragKind(event) {
  const types = [...(event.dataTransfer?.types || [])];
  if (!types.includes('Files')) return null;
  const item = [...(event.dataTransfer?.items || [])].find((i) => i.kind === 'file');
  const type = (item?.type || '').toLowerCase();
  if (/^audio\/(x-)?mid(i)?$/.test(type)) return kindById('midi');
  if (type.startsWith('audio/')) return kindById('audio');
  return undefined;   // a file whose kind is only known by its name, at the drop
}

/** The project folder a file that comes from outside is uploaded into, by kind. */
export const UPLOAD_FOLDER = { midi: 'assets/midi', audio: 'assets/audio', soundfont: 'samples', lyrics: 'lyrics' };
