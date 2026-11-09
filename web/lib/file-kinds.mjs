// What kind of file a path is, and which block parameters take it. One table for the project panel menus ("Use in song"), the code editor,
// the property fields and the canvas (plan project-panel PP01, PP03). Pure: no DOM, no network. @ample:file-kinds

/**
 * The kinds of file a block can read. `ext` are lower-case extensions with the dot; `block` is the block type a drop creates and `param` the parameter that gets the path.
 * A kind without `block` has a special insert (see `blockText`).
 */
export const FILE_KINDS = [
  { id: 'midi', label: 'MIDI file', ext: ['.mid', '.midi'], block: 'midi', param: 'file' },
  { id: 'audio', label: 'Audio file', ext: ['.wav', '.flac', '.mp3', '.ogg', '.m4a', '.aif', '.aiff'], block: 'import-audio', param: 'file' },
  { id: 'soundfont', label: 'SoundFont', ext: ['.sf2'], block: 'gm', param: 'file' },
  { id: 'lyrics', label: 'Lyrics file', ext: ['.lyr', '.lrc', '.amplelrc', '.txt'], block: 'lyrics', param: 'file' },
  { id: 'sequence', label: 'Sequence', ext: ['.ampleseq', '.seq'], block: 'sequencer', param: 'file' },
  { id: 'module', label: 'Module', ext: ['.module'], block: null, param: null },
  { id: 'song', label: 'Song', ext: ['.amplet'], block: null, param: null },
  { id: 'folder', label: 'Folder', ext: [], block: 'drums', param: 'kit' },
];

const byId = new Map(FILE_KINDS.map((k) => [k.id, k]));
export const kindById = (id) => byId.get(id) || null;

/** The extension of a path in lower case, with the dot ('' when there is none). */
export const extensionOf = (path) => { const m = /(\.[A-Za-z0-9]+)$/.exec(String(path || '')); return m ? m[1].toLowerCase() : ''; };

/** The kind of a path, or null: a folder is the kind `folder`, a file by its extension. */
export function kindOf(path, isDir = false) {
  if (isDir) return byId.get('folder');
  const ext = extensionOf(path);
  return ext ? FILE_KINDS.find((k) => k.ext.includes(ext)) || null : null;
}

/** Whether a template parameter takes files of this kind: a `file` field whose accepted extensions include the kind's, or a `sampleset` field for folders. */
export function paramAccepts(param, kind) {
  if (!param || !kind) return false;
  if (kind.id === 'folder') return param.kind === 'sampleset';
  if (param.kind !== 'file') return false;
  const accept = (param.accept || []).map((e) => e.toLowerCase());
  return accept.length > 0 && kind.ext.some((e) => accept.includes(e));
}

/** The block types (with the parameter) that take the kind, the preferred one first. templates: S.templates (id, class, version, parameters). */
export function blocksFor(kind, templates) {
  if (!kind) return [];
  const out = [];
  for (const t of templates || []) {
    for (const p of t.parameters || []) {
      if (paramAccepts(p, kind)) out.push({ block: t.id, title: t.title, param: p.name });
    }
  }
  out.sort((a, b) => (b.block === kind.block) - (a.block === kind.block) || a.block.localeCompare(b.block));
  return out;
}

/** A path as a parameter value: plain when it is a simple word, quoted when it has spaces or other characters. */
export function formatValue(path) {
  const v = String(path);
  return /^[A-Za-z0-9_./:@+-]+$/.test(v) ? v : `"${v.replace(/"/g, '\\"')}"`;
}

/** A block name from a file name: letters, digits, `_` and `-`, free among the existing names. */
export function freeBlockName(path, existing) {
  const base = (String(path).split('/').pop() || 'block').replace(/\.[A-Za-z0-9]+$/, '').replace(/[^A-Za-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '') || 'block';
  const taken = new Set(existing || []);
  if (!taken.has(base)) return base;
  for (let i = 2; ; i++) if (!taken.has(`${base}-${i}`)) return `${base}-${i}`;
}

/** The text of a new block for a path: `type name {` with the file parameter, or a `use` line for a module. null when the kind creates nothing. */
export function blockText(kind, path, { template, name, instance }) {
  if (!kind) return null;
  if (kind.id === 'module') {
    const module = (path.split('/').pop() || '').replace(/\.module$/i, '');
    return `use ${module} as ${instance || module} {\n}\n`;
  }
  if (!template || !kind.param) return null;
  const param = kind.id === 'folder' ? 'kit' : kind.param;
  return `${template.id} ${name} {\n  ${param} ${formatValue(path)}\n}\n`;
}
