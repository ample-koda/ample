import { kindOf } from '../lib/file-kinds.mjs';

const paths = {
  folder: '<path d="M3 6.5h7l2 2H21v9H3z" fill="currentColor" fill-opacity=".16"/><path d="M3 6.5h7l2 2H21v9H3z"/>',
  folderOpen: '<path d="M3 18V6h7l2 2h8v3M3 18l3-7h16l-3 7z" fill="currentColor" fill-opacity=".16"/><path d="M3 18V6h7l2 2h8v3M3 18l3-7h16l-3 7z"/>',
  // a song (.amplet): a page with a folded corner in the neon colours, the A with its white wave inside and the teal note in the top left corner
  song: '<defs><linearGradient id="ample-song-neon" gradientUnits="userSpaceOnUse" x1="4" y1="3" x2="20" y2="21"><stop offset="0" stop-color="#2de2ff"/><stop offset="1" stop-color="#ff4fd8"/></linearGradient></defs><path d="M6 3h8l4 4v14H6zM14 3v4h4" stroke="url(#ample-song-neon)"/><path d="M9.2 18.4 12.2 10.6l3 7.8" stroke="url(#ample-song-neon)" stroke-width="1.9"/><path d="M10.3 15.9q.7-1.9 1.4 0t1.4 0" stroke="#ffffff" stroke-width="1.5"/><circle cx="9" cy="6.6" r="1.35" fill="#19e3c8" stroke="none"/><path d="M10.1 6.3V4.4" stroke="#19e3c8" stroke-width="1.2"/>',
  audio: '<path d="M3 10v4m4-7v10m4-13v16m4-13v10m4-7v4"/>',
  midi: '<rect x="3" y="5" width="18" height="14" rx="1"/><path d="M7 5v8m4-8v8m4-8v8m4-8v8M5 13h14"/>',
  lyrics: '<path d="M6 5h12M6 9h12M6 13h8M6 17h10"/>',
  module: '<circle cx="6" cy="12" r="2.5"/><circle cx="18" cy="6" r="2.5"/><circle cx="18" cy="18" r="2.5"/><path d="m8.3 10.8 7.4-3.6m-7.4 6 7.4 3.6"/>',
  pack: '<path d="M4 8h16v11H4zM3 5h18v3H3zM10 11h4v4h-4z"/>',
  file: '<path d="M6 3h8l4 4v14H6zM14 3v5h5M9 12h6m-6 4h6"/>',
  // the top level of the project: the AMPLE A in the neon colours of the app icon (cyan to magenta), a white wave for its crossbar and a teal dot
  projectRoot: '<defs><linearGradient id="ample-root-neon" gradientUnits="userSpaceOnUse" x1="4" y1="12" x2="20" y2="12"><stop offset="0" stop-color="#2de2ff"/><stop offset="1" stop-color="#ff4fd8"/></linearGradient></defs><path d="M5 20 12 4l7 16" stroke="url(#ample-root-neon)"/><path d="M7.6 14.8q1-3 2.1 0t2.1 0t2.1 0t2.1 0" stroke="#ffffff"/><circle cx="19.4" cy="5.2" r="1.6" fill="#19e3c8" stroke="none"/>',
};

function iconKind(item) {
  if (item?.isDir && item.path === '') return 'projectRoot';
  if (item?.isDir) return item.expanded ? 'folderOpen' : 'folder';
  const kind = item?.kind || kindOf(item?.path || item?.name)?.id;
  return ({ chords: 'lyrics', note: 'file', sequence: 'midi', soundfont: 'midi', text: 'lyrics', data: 'file', pack: 'pack' })[kind] || (paths[kind] ? kind : 'file');
}

export function projectItemIcon(item) {
  const kind = iconKind(item);
  const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  icon.setAttribute('class', `project-item-icon project-item-icon-${kind === 'folderOpen' ? 'folder' : kind}`);
  icon.setAttribute('viewBox', '0 0 24 24');
  icon.setAttribute('aria-hidden', 'true');
  icon.setAttribute('fill', 'none');
  icon.setAttribute('stroke', 'currentColor');
  icon.setAttribute('stroke-width', '1.7');
  icon.setAttribute('stroke-linecap', 'round');
  icon.setAttribute('stroke-linejoin', 'round');
  icon.setAttribute('focusable', 'false');
  icon.innerHTML = paths[kind];
  return icon;
}
