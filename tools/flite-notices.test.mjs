import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

// The Flite licence says that a modified version must be clearly marked and that the notice stays. AMPLE translates the Flite program to C# and converts its data, so
// every translated file carries the marking, the licence text names every copyright holder, and the third-party list names them too.
const root = new URL('../', import.meta.url);
const flite = new URL('src/Ample.Speech/Flite/', root);
const translated = ['Core', 'Text', 'Lexicon', 'Prosody', 'Synth', 'Cg'];

function sources(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? sources(path.join(dir, e.name)) : e.name.endsWith('.cs') ? [path.join(dir, e.name)] : []));
}

test('every translated source file says that it is a modified port of Flite', () => {
  const missing = [];
  for (const part of translated) {
    for (const file of sources(new URL(part + '/', flite).pathname.replace(/^\/([A-Za-z]:)/, '$1'))) {
      const head = fs.readFileSync(file, 'utf8').split('\n').slice(0, 14).join('\n');
      const marked = /Port of (the string conventions of )?Flite \(CMU/.test(head) && /Translated to C# and modified/.test(head);
      if (!marked) missing.push(path.relative(new URL('.', root).pathname.replace(/^\/([A-Za-z]:)/, '$1'), file));
    }
  }

  assert.deepEqual(missing, []);
});

test('the licence text names every holder of the code and of the voice', () => {
  const text = fs.readFileSync(new URL('LICENSES/flite.txt', root), 'utf8');
  for (const holder of ['Carnegie Mellon University', 'Alan W Black and Kevin Lenzo', 'University of Edinburgh', 'Nagoya Institute of Technology']) assert.ok(text.includes(holder), holder);
  assert.match(text, /not\s+endorsed by Carnegie Mellon University/);
});

test('the third-party list and the data notice name the voice and the licence file', () => {
  const list = fs.readFileSync(new URL('THIRD-PARTY-NOTICES.md', root), 'utf8');
  assert.match(list, /Flite speech synthesis/);
  assert.match(list, /Alan W Black and Kevin Lenzo/);
  assert.match(list, /LICENSES\/flite\.txt/);
  const data = fs.readFileSync(new URL('src/Ample.Speech/Flite/Data/FLITE-NOTICE.md', root), 'utf8');
  assert.match(data, /These files are modified/);
  assert.match(data, /Festival licence/);
});

test('the licence of the recordings of the voices slt, rms and awb names its holders and the three voices, and the lists point to it', () => {
  const text = fs.readFileSync(new URL('LICENSES/cmu-arctic.txt', root), 'utf8');
  for (const holder of ['Carnegie Mellon University', 'Alan W Black', 'Kevin A. Lenzo', 'John Kominek']) assert.ok(text.includes(holder), holder);
  for (const voice of ['cmu_us_slt', 'cmu_us_rms', 'cmu_us_awb']) assert.ok(text.includes(voice), voice);
  assert.match(text, /All voice talents have signed a waiver/);
  assert.match(text, /rms is covered by the Flite distribution terms; the ARCTIC 2003 report lists four other speakers/);
  assert.match(text, /not endorsed by Carnegie Mellon University/);
  const list = fs.readFileSync(new URL('THIRD-PARTY-NOTICES.md', root), 'utf8');
  assert.match(list, /cmu_us_slt/);
  assert.match(list, /LICENSES\/cmu-arctic\.txt/);
  const data = fs.readFileSync(new URL('src/Ample.Speech/Flite/Cg/Data/FLITE-CG-NOTICE.md', root), 'utf8');
  assert.match(data, /These files are modified/);
  assert.match(data, /LICENSES\/cmu-arctic\.txt/);
  assert.doesNotMatch(data, /Not shipped, not public/);
});

test('no Flite source uses a fused multiply-add or a single-precision math library call (the JIT must give the same bits on x64 and arm64)', () => {
  const found = [];
  for (const file of sources(new URL('.', flite).pathname.replace(/^\/([A-Za-z]:)/, '$1'))) {
    const text = fs.readFileSync(file, 'utf8');
    if (/FusedMultiplyAdd|MathF\.|Vector\d*\.|System\.Runtime\.Intrinsics/.test(text)) found.push(file);
  }

  assert.deepEqual(found, []);
});
