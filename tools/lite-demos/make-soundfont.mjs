// Builds the small SoundFont of the Lite demo "gm-sf2" from CC0 samples (VSCO 2 CE upright piano, audio-packs/free/vsco-2-ce): nine notes, each cut to three seconds with a
// fade-out, one preset (bank 0, program 0), one instrument zone per note. No third-party SoundFont is used, so the file is CC0 too. It goes to web/remix/assets/piano-sf2/ with
// OWN.json (provenance for make-assets.mjs) and LICENSE.txt / SOURCE.md.
// Usage: node tools/lite-demos/make-soundfont.mjs   (needs the pack: node tools/fetch-audio-packs.mjs vsco-2-ce)

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const pack = path.join(root, 'audio-packs/free/vsco-2-ce');
const out = path.join(root, 'web/remix/assets/piano-sf2');
const NOTES = ['a1', 'f2', 'a2', 'f3', 'a3', 'f4', 'a4', 'f5', 'a5'];
const SECONDS = 3;
const NAMES = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 };

const midiOf = (name) => {
  const m = /^([a-g])(#?)(\d)$/.exec(name);
  return 12 * (Number(m[3]) + 1) + NAMES[m[1]] + (m[2] ? 1 : 0);
};

function readWav(file) {
  const b = fs.readFileSync(file);
  let pos = 12, rate = 44100, bits = 16, channels = 1, data = null;
  while (pos + 8 <= b.length) {
    const id = b.toString('latin1', pos, pos + 4), size = b.readUInt32LE(pos + 4);
    if (id === 'fmt ') { channels = b.readUInt16LE(pos + 10); rate = b.readUInt32LE(pos + 12); bits = b.readUInt16LE(pos + 22); }
    if (id === 'data') data = b.subarray(pos + 8, pos + 8 + size);
    pos += 8 + size + (size & 1);
  }
  if (!data || bits !== 16 || channels !== 1) throw new Error(`${file}: expected 16-bit mono WAV`);
  return { rate, samples: new Int16Array(data.buffer.slice(data.byteOffset, data.byteOffset + data.length - (data.length & 1))) };
}

const chunk = (id, body) => { const h = Buffer.alloc(8); h.write(id, 0, 'latin1'); h.writeUInt32LE(body.length, 4); return Buffer.concat([h, body, body.length & 1 ? Buffer.alloc(1) : Buffer.alloc(0)]); };
const list = (type, ...chunks) => chunk('LIST', Buffer.concat([Buffer.from(type, 'latin1'), ...chunks]));
const str = (s, n) => { const b = Buffer.alloc(n); b.write(s, 0, 'latin1'); return b; };
const u16 = (...v) => { const b = Buffer.alloc(v.length * 2); v.forEach((x, i) => b.writeUInt16LE(x & 0xffff, i * 2)); return b; };
const gen = (oper, amount) => u16(oper, amount);

const samples = NOTES.map((n) => {
  const wav = readWav(path.join(pack, 'upright-piano', `${n}.wav`));
  const length = Math.min(wav.samples.length, wav.rate * SECONDS);
  const cut = wav.samples.slice(0, length);
  const fade = Math.floor(wav.rate * 0.5);
  for (let i = 0; i < fade; i++) cut[length - 1 - i] = Math.round(cut[length - 1 - i] * (i / fade));
  return { name: n, key: midiOf(n), rate: wav.rate, data: cut };
});

const PAD = 46;
let smpl = Buffer.alloc(0);
const shdr = [];
for (const s of samples) {
  const start = smpl.length / 2;
  smpl = Buffer.concat([smpl, Buffer.from(s.data.buffer, s.data.byteOffset, s.data.byteLength), Buffer.alloc(PAD * 2)]);
  const h = Buffer.alloc(46);
  h.write(`piano ${s.name}`, 0, 'latin1');
  h.writeUInt32LE(start, 20); h.writeUInt32LE(start + s.data.length, 24); h.writeUInt32LE(start, 28); h.writeUInt32LE(start, 32);
  h.writeUInt32LE(s.rate, 36); h.writeUInt8(s.key, 40); h.writeInt8(0, 41); h.writeUInt16LE(0, 42); h.writeUInt16LE(1, 44);
  shdr.push(h);
}
const eos = Buffer.alloc(46); eos.write('EOS', 0, 'latin1'); shdr.push(eos);

// instrument zones: each note covers the keys from just above the previous root's midpoint up to its own midpoint with the next
const ibag = [], igen = [];
samples.forEach((s, i) => {
  const lo = i === 0 ? 0 : Math.floor((samples[i - 1].key + s.key) / 2) + 1;
  const hi = i === samples.length - 1 ? 127 : Math.floor((s.key + samples[i + 1].key) / 2);
  ibag.push(u16(igen.length, 0));
  igen.push(gen(43, lo | (hi << 8)), gen(58, s.key), gen(54, 0), gen(53, i));
});
ibag.push(u16(igen.length, 0));
igen.push(gen(0, 0));

const inst = Buffer.concat([str('Upright piano', 20), u16(0), str('EOI', 20), u16(samples.length)]);
const phdr = Buffer.concat([str('Upright piano', 20), u16(0, 0, 0), Buffer.alloc(12), str('EOP', 20), u16(0, 0, 1), Buffer.alloc(12)]);
const pbag = Buffer.concat([u16(0, 0), u16(1, 0)]);
const pgen = Buffer.concat([gen(41, 0), gen(0, 0)]);
const pmod = Buffer.alloc(10), imod = Buffer.alloc(10);

const info = list('INFO', chunk('ifil', u16(2, 1)), chunk('isng', str('EMU8000', 8)), chunk('INAM', str('AMPLE Remix upright piano', 32)), chunk('ICOP', str('CC0 1.0 (samples: VSCO 2 CE)', 32)));
const sdta = list('sdta', chunk('smpl', smpl));
const pdta = list('pdta', chunk('phdr', phdr), chunk('pbag', pbag), chunk('pmod', pmod), chunk('pgen', pgen), chunk('inst', inst), chunk('ibag', Buffer.concat(ibag)), chunk('imod', imod), chunk('igen', Buffer.concat(igen)), chunk('shdr', Buffer.concat(shdr)));
const body = Buffer.concat([Buffer.from('sfbk', 'latin1'), info, sdta, pdta]);
const riff = Buffer.concat([Buffer.from('RIFF', 'latin1'), (() => { const b = Buffer.alloc(4); b.writeUInt32LE(body.length); return b; })(), body]);

fs.rmSync(out, { recursive: true, force: true });
fs.mkdirSync(out, { recursive: true });
fs.writeFileSync(path.join(out, 'piano.sf2'), riff);
fs.copyFileSync(path.join(pack, 'LICENSE.txt'), path.join(out, 'LICENSE.txt'));
fs.writeFileSync(path.join(out, 'SOURCE.md'), `# AMPLE Remix upright piano (SoundFont)\n\n- Source: https://github.com/sgossner/VSCO-2-CE\n- Licence: CC0 1.0 (the upright piano of VSCO 2 Community Edition, Versilian Studios; see LICENSE.txt)\n- Nine notes (${NOTES.join(', ')}), each cut to ${SECONDS} seconds with a fade-out, put in one SF2 preset (bank 0, program 0) by tools/lite-demos/make-soundfont.mjs. No other SoundFont is used.\n`);
fs.writeFileSync(path.join(out, 'OWN.json'), JSON.stringify({ what: 'a small SoundFont: an upright piano from nine CC0 recordings', source: 'https://github.com/sgossner/VSCO-2-CE', licence: 'CC0 1.0' }, null, 2) + '\n');
console.log(`piano.sf2 ${(riff.length / 1024).toFixed(0)} KB, ${samples.length} samples`);
