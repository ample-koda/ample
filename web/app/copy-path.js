// Copying the name or the path of a project file to the clipboard: the name, the path in the project, and the full path on the disk of the project.
// Used by the menu of the Project tab and by the preview window of a sound. @ample:copy-path

import { S } from './store.js';
import { toast } from './ui.js';

/** The full path of a project file on the disk where the project lives (with the separator of that disk). */
export function fullPath(path) {
  const folder = String(S.project?.folder || '');
  if (!folder) return path;
  const sep = folder.includes('\\') ? '\\' : '/';
  return `${folder.replace(/[\\/]+$/, '')}${sep}${String(path).split('/').filter(Boolean).join(sep)}`;
}

/** Puts text on the clipboard (with a fallback for a page that may not use the clipboard API) and says what was copied. */
export async function copyText(text, what) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const area = Object.assign(document.createElement('textarea'), { value: text });
    area.style.cssText = 'position:fixed;left:-9999px;top:0';
    document.body.append(area);
    area.select();
    const ok = document.execCommand('copy');
    area.remove();
    if (!ok) { toast(`Could not copy the ${what}.`); return false; }
  }
  toast(`Copied the ${what}: ${text}`);
  return true;
}

/** The Copy submenu of a project file or folder: its name, its path in the project and its full path. */
export function copyMenuItem(path) {
  const name = String(path).split('/').filter(Boolean).pop() || path;
  return {
    label: 'Copy',
    children: [
      { label: 'Name', title: name, fn: () => copyText(name, 'name') },
      { label: 'Path in the project', title: path, fn: () => copyText(path, 'path') },
      { label: 'Full path', title: fullPath(path), fn: () => copyText(fullPath(path), 'full path') },
    ],
  };
}
