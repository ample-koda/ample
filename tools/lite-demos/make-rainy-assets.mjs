// Makes the sound pack "rainy-cars" of the Lite demo "Cars passing the music" from two CC0 recordings (Freesound):
// a stretch of rain (BonnyOrbit, "Light rain on street") and a car that passes on a wet road (Breviceps, "Passing Car (Wet road)").
// Usage: node tools/lite-demos/make-rainy-assets.mjs <folder with rain-light.wav and car-pass.wav (16 bit stereo 44.1 kHz)>
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const source = process.argv[2];
if (!source) throw new Error('give the folder with rain-light.wav and car-pass.wav');
const target = path.join(root, 'web/remix/assets/rainy-cars');
fs.mkdirSync(target, { recursive: true });

function readWav(file) {
  const b = fs.readFileSync(file);
  let at = 12, fmt = null, data = null;
  while (at + 8 <= b.length) {
    const id = b.toString('ascii', at, at + 4), size = b.readUInt32LE(at + 4);
    if (id === 'fmt ') fmt = { channels: b.readUInt16LE(at + 10), rate: b.readUInt32LE(at + 12), bits: b.readUInt16LE(at + 22) };
    if (id === 'data') { data = b.subarray(at + 8, at + 8 + size); break; }
    at += 8 + size + (size & 1);
  }
  if (!fmt || !data || fmt.bits !== 16) throw new Error('not a 16 bit wav: ' + file);
  return { ...fmt, data };
}
const monoSamples = ({ channels, data }) => { const n = data.length / 2 / channels; const out = new Int16Array(n); for (let i = 0; i < n; i++) { let s = 0; for (let c = 0; c < channels; c++) s += data.readInt16LE((i * channels + c) * 2); out[i] = Math.round(s / channels); } return out; };
const writeWav = (file, samples, rate) => { const b = Buffer.alloc(44 + samples.length * 2); b.write('RIFF', 0); b.writeUInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20); b.writeUInt16LE(1, 22); b.writeUInt32LE(rate, 24); b.writeUInt32LE(rate * 2, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34); b.write('data', 36); b.writeUInt32LE(samples.length * 2, 40); samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2)); fs.writeFileSync(file, b); };

const rain = readWav(path.join(source, 'rain-light.wav'));
const rainMono = monoSamples(rain);
writeWav(path.join(target, 'rain.wav'), rainMono.subarray(20 * rain.rate, 44 * rain.rate), rain.rate);   // 24 seconds from the middle
const car = readWav(path.join(source, 'car-pass.wav'));
writeWav(path.join(target, 'car.wav'), monoSamples(car), car.rate);
fs.writeFileSync(path.join(target, 'LICENSE.txt'), 'Both recordings are Creative Commons 0 (public domain dedication), checked on the Freesound page of each sound on 2026-10-14.\n');
fs.writeFileSync(path.join(target, 'SOURCE.md'), `# Rainy cars

- rain.wav: 24 seconds of "Light rain on street.wav" by BonnyOrbit, https://freesound.org/people/BonnyOrbit/sounds/380651/ (CC0), mixed to mono.
- car.wav: "Passing Car (Wet road)" by Breviceps, https://freesound.org/people/Breviceps/sounds/462862/ (CC0), mixed to mono.
- Made with tools/lite-demos/make-rainy-assets.mjs: 16-bit mono 44.1 kHz WAV.
`);
const indexFile = path.join(root, 'web/remix/assets/index.json');
const index = JSON.parse(fs.readFileSync(indexFile, 'utf8')).filter((p) => p.pack !== 'rainy-cars');
index.push({ pack: 'rainy-cars', what: 'rain and a car that passes on a wet road (two public domain recordings)', source: 'https://freesound.org/people/Breviceps/sounds/462862/', licence: 'CC0 1.0 (public domain dedication)', files: ['rain.wav', 'car.wav'].map((file) => ({ file, bytes: fs.statSync(path.join(target, file)).size })) });
fs.writeFileSync(indexFile, JSON.stringify(index, null, 2) + '\n');
console.log('rainy-cars:', index.at(-1).files.map((f) => `${f.file} ${f.bytes}`).join(', '));
