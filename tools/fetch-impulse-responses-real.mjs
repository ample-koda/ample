#!/usr/bin/env node
// Fetches the "impulse-responses-real" audio pack: five CC0 impulse responses from Freesound (the hq preview of each sound, converted to 16 bit mono 44.1 kHz WAV with ffmpeg).
// Each licence was read from the sound's own page on 2026-10-14. Use: file pack:impulse-responses-real/<name>, or the convolution ir option real-<name>.
// Usage: node tools/fetch-impulse-responses-real.mjs [target-folder]   (default: <user ample home>/audio-packs/free/impulse-responses-real)
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const target = process.argv[2] ?? path.join(process.env.AMPLE_HOME ?? path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), '.local', 'share'), 'ample'), 'audio-packs', 'free', 'impulse-responses-real');
const sounds = [
  { name: 'stairwell', id: 706562, user: 13595287, title: 'Stairwell impulse response wood spring', author: 'wildestshockt', page: 'https://freesound.org/people/wildestshockt/sounds/706562/' },
  { name: 'garage', id: 868385, user: 16682330, title: 'IR_Two_Car_Garage_HandClap_02', author: 'loganzsound', page: 'https://freesound.org/people/loganzsound/sounds/868385/' },
  { name: 'car', id: 555787, user: 10147844, title: 'Car Interior Impulse Response 2', author: 'jamespeacock4616', page: 'https://freesound.org/people/jamespeacock4616/sounds/555787/' },
  { name: 'basement', id: 382908, user: 2812020, title: 'Little Basement (iR - Impulse Response).wav', author: 'Uzbazur', page: 'https://freesound.org/people/Uzbazur/sounds/382908/' },
  { name: 'microverb', id: 414189, user: 5347841, title: 'Alesis Microverb IR Small 4', author: 'joeribraams', page: 'https://freesound.org/people/joeribraams/sounds/414189/' },
];

fs.mkdirSync(target, { recursive: true });
for (const s of sounds) {
  const url = `https://cdn.freesound.org/previews/${Math.floor(s.id / 1000)}/${s.id}_${s.user}-hq.mp3`;
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${s.name}: ${response.status} for ${url}`);
  const temp = path.join(os.tmpdir(), `ir-${s.id}.mp3`);
  fs.writeFileSync(temp, Buffer.from(await response.arrayBuffer()));
  execFileSync('ffmpeg', ['-v', 'error', '-y', '-i', temp, '-ac', '1', '-ar', '44100', '-c:a', 'pcm_s16le', path.join(target, `${s.name}.wav`)]);
  fs.rmSync(temp, { force: true });
}

fs.writeFileSync(path.join(target, 'LICENSE.txt'), 'CC0 1.0 Universal (public domain dedication). Each file is a CC0 sound from Freesound; see SOURCE.md for the pages. No attribution is required.\n');
fs.writeFileSync(path.join(target, 'SOURCE.md'), `# Impulse responses (real, CC0)\n\n- Tier: free\n- Fetched with: tools/fetch-impulse-responses-real.mjs (hq preview, converted to mono 44.1 kHz WAV)\n- Licence: CC0 1.0, read from each sound page on 2026-10-14\n\n| File | Title | Author | Page |\n|---|---|---|---|\n${sounds.map((s) => `| ${s.name}.wav | ${s.title} | ${s.author} | ${s.page} |`).join('\n')}\n`);
console.log(`impulse-responses-real: ${sounds.length} files in ${target}`);
