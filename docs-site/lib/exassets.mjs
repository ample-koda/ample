// The example song of a block as the docs play it: the spec example (spec/templates/<block>.json) with, for blocks that need files, the free CC0 files
// of docs-site/example-assets/ (see ATTRIBUTION.md there). examples.json: files {project path -> file in example-assets/files/ (a trailing / = a folder)}, replace [[from, to]] (spec text -> text of the docs), append (text added), note, noRemix.
import fs from 'node:fs'; import path from 'node:path'; import { fileURLToPath } from 'node:url';
export const dir = path.join(path.dirname(fileURLToPath(import.meta.url)), '../example-assets');
const cfg = JSON.parse(fs.readFileSync(path.join(dir, 'examples.json'), 'utf8'));
export const noDemo = (id) => !!(cfg[id] && cfg[id].noDemo);
/* the id of a rendered example: its song text plus rev (raise rev in examples.json when only the files of the example changed, so that the mp3 gets a new name) */
export const exKey = (id, code) => code + (cfg[id] && cfg[id].rev ? 'rev' + cfg[id].rev : '');
export const hasConfig = (id) => !!cfg[id];
export function exampleOf(id, code) {
  const c = cfg[id]; if (!c) return { code, key: code, files: [], note: '', noRemix: false };
  let t = code; for (const [a, b] of c.replace || []) { if (!t.includes(a)) throw new Error('example-assets/examples.json: "' + a + '" not in the example of ' + id); t = t.split(a).join(b); }
  t += c.append || '';
  const files = []; for (const [to, from] of Object.entries(c.files || {})) { if (from.endsWith('/')) for (const f of fs.readdirSync(path.join(dir, 'files', from)).sort()) files.push({ to: to + f, from: from + f }); else files.push({ to, from }); }
  return { code: t, key: exKey(id, t), files, note: c.note || '', noRemix: !!c.noRemix || files.length > 0 };
}
