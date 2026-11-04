import test from 'node:test';
import assert from 'node:assert/strict';
import { embedFile, extractToFile, readEmbedded, sequencePart, withEmbedded } from '../embedded-seq.mjs';

const seq = 'sequencer 1\nchannel kick drum sample=samples/kick.wav\n\npattern v bars=1\n  kick x...\n';
const withFile = 'composition "x" { ample 1  tempo 100  master-seed 1 }\nsequencer sq at 1 for 4 {\n  file sequences/a.ampleseq\n  lanes lane1=kick\n}\nlayout sq 10 20\n';

test('a sequence is moved into the song and read back as the text of the file', () => {
  const embedded = embedFile(withFile, 1, seq);
  assert.match(embedded, /^  sequence \{\n    sequencer 1\n    channel kick drum/m);
  assert.doesNotMatch(embedded, /file sequences/);
  assert.match(embedded, /  lanes lane1=kick\n\}\nlayout sq 10 20/);   // what else the block and the song have stays
  assert.equal(readEmbedded(embedded, 1), seq.replace(/\s+$/, ''));
});

test('the sequence in the song can be changed, and extracted to a file name', () => {
  const embedded = embedFile(withFile, 1, seq);
  const changed = withEmbedded(embedded, 1, `${seq}channel hat drum sample=samples/hat.wav\n`);
  assert.match(changed, /    channel hat drum/);
  assert.equal(changed.split('sequence {').length, 2);   // replaced, not added a second time
  const out = extractToFile(changed, 1, 'sequences/my beat.ampleseq');
  assert.match(out, /  file "sequences\/my beat.ampleseq"\n  lanes lane1=kick/);
  assert.equal(readEmbedded(out, 1), null);
  assert.equal(extractToFile(withFile, 1, 'x'), withFile);   // nothing to extract
});

test('a block without a file or a sequence gets the sequence at its end; the next block is left alone', () => {
  const text = 'sequencer a {\n  lanes lane1=kick\n}\nsequencer b {\n  file x.ampleseq\n}\n';
  const added = withEmbedded(text, 0, seq);
  assert.match(added, /  lanes lane1=kick\n  sequence \{\n    sequencer 1/);
  assert.match(added, /\}\nsequencer b \{\n  file x.ampleseq\n\}/);
  assert.equal(readEmbedded(added, 3), null);
  const part = sequencePart(added, 0);
  assert.ok(part.open > 0 && part.close > part.open && part.blockEnd > part.close);
});
