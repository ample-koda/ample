import test from 'node:test';
import assert from 'node:assert/strict';
import { arrangementSubject, barSubject, branchSubject, explainLocally, promptOf, revisionSubject, subjectName } from '../koda-context.mjs';

test('a revision, a branch, a bar and the arrangement are subjects with a name and a prompt', () => {
  const rev = revisionSubject({ id: 'abc123', message: 'Before AI change', line: 'garage', ref: 'AR-1597', at: '2026-10-22 20:00' });
  assert.equal(subjectName(rev), 'the revision AR-1597');
  assert.match(promptOf(rev, 'what changed here?', 'explain'), /AR-1597 \(id abc123\), message "Before AI change", on the branch "garage".*ample_revision with id=abc123/);
  const br = branchSubject({ name: 'garage', current: true });
  assert.equal(subjectName(br), 'the branch garage');
  assert.match(promptOf(br, '', 'explain'), /the one they are on/);
  const bar = barSubject({ block: 'drums', at: 9, length: 8, name: 'fill' });
  assert.equal(subjectName(bar), 'drums at bar 9');
  assert.match(promptOf(bar, 'make it shorter', 'run'), /bar 9 for 8 bars \(fill\)/);
  assert.equal(subjectName(arrangementSubject({ bar: 5 })), 'the arrangement at bar 5');
  assert.match(promptOf(arrangementSubject({}), 'x', 'run'), /the arrangement/);
});

test('these subjects need the model: nothing is answered locally', () => {
  for (const s of [revisionSubject({ id: 'a' }), branchSubject({ name: 'b' }), barSubject({ block: 'c', at: 1, length: 1 }), arrangementSubject({})]) assert.equal(explainLocally(s, {}), null);
});

test('several things linked at once are described together, and named together', async () => {
  const { multiSubject, promptOf, subjectName } = await import('../koda-context.mjs');
  const m = multiSubject([{ kind: 'block', block: 'piano', type: 'sequencer' }, { kind: 'file', path: 'songs/a.amplet', isDir: false }]);
  const text = promptOf(m, 'how do these fit?', 'explain');
  assert.match(text, /2 things together/);
  assert.match(text, /1\. The user is looking at the block piano/);
  assert.match(text, /2\. The user is looking at the file songs\/a\.amplet/);
  assert.equal(subjectName(m), 'piano and a.amplet');
});

test('the model gives a title; the answer form is part of a question but not of an instruction', async () => {
  const { splitTitle, promptOf, ANSWER_FORM } = await import('../koda-context.mjs');
  assert.deepEqual(splitTitle('Title: The wet reverb chain\nThe strings go to the reverb.\n\nMore details here.'), { title: 'The wet reverb chain', text: 'The strings go to the reverb.\n\nMore details here.' });
  assert.deepEqual(splitTitle('# Short title\nBody'), { title: 'Short title', text: 'Body' });
  assert.deepEqual(splitTitle('No title here.'), { title: '', text: 'No title here.' });
  const block = { kind: 'block', block: 'piano', type: 'sequencer' };
  assert.ok(promptOf(block, 'what?', 'explain').includes(ANSWER_FORM));
  assert.ok(!promptOf(block, 'make it louder', 'run').includes(ANSWER_FORM));
});

test('a long answer that came as one block is split: the first sentences, and the rest to unfold', async () => {
  const { splitLong } = await import('../koda-context.mjs');
  const long = 'The bass is ducked. The guitar is filtered and sent to reverb. Swelling strings join the same path around the breakdown. The mixer combines those sounds with drums and keys for the premaster. The low-pass filter cuts above 1,800 Hz and the reverb has a large size and a long decay. The string level rises into bar 47 and fades out by bar 49.';
  const [text, more] = splitLong(long);
  assert.equal(text, 'The bass is ducked. The guitar is filtered and sent to reverb. Swelling strings join the same path around the breakdown.');
  assert.ok(more.startsWith('The mixer combines'));
  assert.deepEqual(splitLong('Short one. Two. Three.'), ['Short one. Two. Three.', '']);
  assert.deepEqual(splitLong(long, 'details'), [long, 'details']);
});

test('a question about several things always asks for the part to unfold', async () => {
  const { multiSubject, promptOf, ANSWER_FORM_MANY } = await import('../koda-context.mjs');
  const m = multiSubject([{ kind: 'block', block: 'a', type: 'x' }, { kind: 'block', block: 'b', type: 'y' }]);
  assert.ok(promptOf(m, 'how do these fit?', 'explain').includes(ANSWER_FORM_MANY));
  assert.ok(!promptOf({ kind: 'block', block: 'a', type: 'x' }, 'what?', 'explain').includes(ANSWER_FORM_MANY));
});

test('the short answer ends where the structure starts, also without a blank line', async () => {
  const { splitAnswer } = await import('../koda-context.mjs');
  const raw = 'The arrangement builds a 64-bar track. The guitar and strings go to a large reverb.\n- `arrangement` sets the sequence.\n- `sf2-bass` plays the bass.\n### Summary\n\nThe sequencer lays out the sections.';
  const [text, more] = splitAnswer(raw);
  assert.equal(text, 'The arrangement builds a 64-bar track. The guitar and strings go to a large reverb.');
  assert.ok(more.startsWith('- `arrangement`'));
  assert.ok(more.includes('### Summary'));
  assert.deepEqual(splitAnswer('One paragraph.\n\nThe rest.'), ['One paragraph.', 'The rest.']);
  assert.deepEqual(splitAnswer('Just one line.'), ['Just one line.', '']);
});

test('the song at the top left is a subject of its own: the whole song', async () => {
  const { songSubject, subjectName, promptOf } = await import('../koda-context.mjs');
  const s = songSubject({ path: 'songs/dare.amplet', name: 'dare_i_preamble' });
  assert.equal(subjectName(s), 'the song dare_i_preamble');
  assert.match(promptOf(s, 'what is this?', 'explain'), /whole song dare_i_preamble/);
});

test('a port is a subject: named, described, and a region says which effects it has passed', async () => {
  const { portSubject, subjectName, promptOf } = await import('../koda-context.mjs');
  const { regionChain, chainText } = await import('../region-chain.mjs');
  const blocks = [
    { name: 'rain', type: 'import-audio', froms: [] },
    { name: 'room', type: 'reverb', froms: [{ block: 'rain', output: 'loop-region', input: 'region_in' }] },
    { name: 'tone', type: 'filter', froms: [{ block: 'room', output: 'region', input: 'region_in' }] },
  ];
  assert.deepEqual(regionChain(blocks, 'rain', false), { source: 'rain', effects: [] });
  assert.deepEqual(regionChain(blocks, 'tone', true), { source: 'rain', effects: ['room', 'tone'] });
  assert.deepEqual(regionChain(blocks, 'tone', false), { source: 'rain', effects: ['room'] });
  assert.equal(chainText(regionChain(blocks, 'tone', true)), '2 effects: room, tone');
  const p = portSubject({ block: 'tone', dir: 'out', name: 'region', type: 'region', title: 'Loop seam', description: 'The processed region.', chain: '2 effects: room, tone' });
  assert.equal(subjectName(p), 'the Loop seam output of tone');
  assert.match(promptOf(p, 'what is this?', 'explain'), /region-merge/);
});

test('a group of blocks is a subject: its name, its blocks and their types', async () => {
  const { groupSubject, subjectName, explainLocally } = await import('../koda-context.mjs');
  const g = groupSubject({ id: 'g1', title: 'Group 1', members: [{ block: 'rain', type: 'import-audio' }, { block: 'level', type: 'volume' }] });
  assert.equal(g.count, 2);
  assert.equal(subjectName(g), 'the group Group 1');
  assert.match(explainLocally(g, {}).text, /rain \(import-audio\), level \(volume\)/);
});

test('a part of the sequencer or of AMPLE Jam is a subject with its window, name and tooltip', async () => {
  const { partSubject, subjectName, explainLocally } = await import('../koda-context.mjs');
  const p = partSubject({ area: 'Sequencer', label: 'snare', hint: 'The snare channel', value: 'drum', context: 'snare' });
  assert.equal(subjectName(p), 'snare of snare (Sequencer)');
  assert.equal(explainLocally(p, {}).text, 'The snare channel');
  assert.equal(subjectName(partSubject({ area: 'AMPLE Jam', label: 'Draw', block: 'hum-copy' })), 'Draw (AMPLE Jam of hum-copy)');
});

test('a proposal that only changes the sequence kept in a block is staged in the editor; one that changes more is not', async () => {
  const { stagedSequence } = await import('../koda-sequence.mjs');
  const { withEmbedded } = await import('../embedded-seq.mjs');
  const plain = 'sequencer sq at 1 for 4 {\n  gain -3\n}\n\nvolume v at 1 for 4 {\n  gain -6\n}';
  const base = withEmbedded(plain, 0, 'one');
  const next = withEmbedded(base, 0, 'two');
  assert.deepEqual(stagedSequence(base, next, 'sq'), { text: 'two' });
  assert.equal(stagedSequence(base, next.replace('gain -6', 'gain -9'), 'sq'), null);
  assert.doesNotThrow(() => stagedSequence(plain, next, 'sq'));   // no sequence in the base: it must not throw
});

test('what a new sequence takes away from the old one is named', async () => {
  const { sequenceLoss, lostAnything, lossText } = await import('../seq-loss.mjs');
  const before = { channels: [{ name: 'kick' }, { name: 'gm' }], patterns: [{ name: 'groove', rows: { kick: 'x', gm: '1:0:1:36:96' } }], clips: [{}, {}] };
  const after = { channels: [{ name: 'kick' }], patterns: [{ name: 'groove', rows: { kick: 'x' } }], clips: [{}] };
  const loss = sequenceLoss(before, after);
  assert.deepEqual(loss, { channels: ['gm'], patterns: [], rows: 1, clips: 1 });
  assert.equal(lostAnything(loss), true);
  assert.match(lossText(loss), /channels gm; 1 filled row; 1 clip/);
  assert.equal(lostAnything(sequenceLoss(before, before)), false);
});

test('what a new sequence took away can be put back, the rest of the new sequence stays', async () => {
  const { restoreLost, sequenceLoss, lostAnything } = await import('../seq-loss.mjs');
  const before = { channels: [{ name: 'kick' }, { name: 'gm' }], patterns: [{ name: 'groove', rows: { kick: 'x', gm: '1:0:1:36:96' } }], clips: [{}] };
  const after = { channels: [{ name: 'kick' }, { name: 'bell' }], patterns: [{ name: 'groove', rows: { kick: 'x', bell: '1:0:1:80:90' } }, { name: 'new', rows: {} }], clips: [{}] };
  const fixed = restoreLost(before, after);
  assert.deepEqual(fixed.channels.map((c) => c.name), ['kick', 'bell', 'gm']);
  assert.equal(fixed.patterns[0].rows.gm, '1:0:1:36:96');
  assert.equal(fixed.patterns[0].rows.bell, '1:0:1:80:90');
  assert.equal(fixed.patterns.length, 2);
  assert.equal(lostAnything(sequenceLoss(before, fixed)), false);
});
