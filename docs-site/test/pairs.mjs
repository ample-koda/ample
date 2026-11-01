import fs from 'node:fs'; import { execFileSync } from 'node:child_process';
const s = JSON.parse(fs.readFileSync('../samples.json', 'utf8')), md5 = (id) => { try { return execFileSync('ffmpeg', ['-v', 'error', '-i', '../site/audio/' + id + '.mp3', '-f', 'md5', '-'], { encoding: 'utf8' }).trim(); } catch { return 'x'; } };
const same = [], n = { pairs: 0 };
for (const w of s.filter((x) => x.fx !== 'dry')) { const d = s.find((y) => y.fx === 'dry' && y.title === w.title && y.id.endsWith('-dry')); if (!d) continue; n.pairs++; if (md5(w.id) === md5(d.id)) same.push(w.fx + ' | ' + w.title); }
console.log('pairs', n.pairs, '| wet = dry:', same.length); console.log(same.join('\n'));
