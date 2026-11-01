// Unpack a bundle of content files in one go (saves tool round-trips).
// Bundle format: a line "=== content/music/basic/01-x.md" starts a file; everything until the next "=== " line is its text.
// Append mode: "=== +content/..." inserts the text before the last {{ample ...}} line of an existing page (or at the end);
//   it is skipped when the first line of the text is already in the page (safe to rerun).
// Usage: node tools/unpack.mjs bundle.txt     (only paths under content/ are accepted; plain entries overwrite the file)
import fs from 'node:fs'; import path from 'node:path';
const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');
const text = fs.readFileSync(process.argv[2], 'utf8').replace(/\r\n/g, '\n'), parts = text.split(/^=== (.+)$/m);
const trim = (s) => s.replace(/^\n+/, '').replace(/\n+$/, '');
let n = 0, over = 0, skipped = 0;
for (let i = 1; i < parts.length; i += 2) {
  let rel = parts[i].trim(); const append = rel[0] === '+'; if (append) rel = rel.slice(1);
  const file = path.resolve(root, rel);
  if (!file.startsWith(path.join(root, 'content') + path.sep)) throw new Error('outside content/: ' + rel);
  const body = trim(parts[i + 1]);
  if (append) {
    const old = fs.readFileSync(file, 'utf8');
    if (old.includes(body.split('\n')[0])) { skipped++; continue; }
    const k = old.lastIndexOf('\n{{ample'), head = (k < 0 ? old : old.slice(0, k)).replace(/\n*$/, '');
    fs.writeFileSync(file, head + '\n\n' + body + '\n' + (k < 0 ? '' : old.slice(k)));
    n++; continue;
  }
  fs.mkdirSync(path.dirname(file), { recursive: true }); if (fs.existsSync(file)) over++;
  fs.writeFileSync(file, body + '\n'); n++;
}
console.log(`wrote ${n} files (${over} overwritten, ${skipped} skipped)`);
