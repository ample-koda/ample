import test from 'node:test';
import assert from 'node:assert/strict';
import { blockSubject, codeSubject, explainLocally, fieldSubject, promptOf, subjectName, summaryOf } from '../koda-context.mjs';

const templates = { piano: { title: 'Piano', description: 'Plays the notes it gets on a piano.', parameters: [{ name: 'humanize', label: 'Humanize', doc: 'A little human feel for a sampled instrument.', default: 0, min: 0, max: 100, unit: '%' }, { name: 'set', doc: 'The built-in piano, or a sample folder.', options: ['felt-piano'] }] } };
const node = { id: 'keys', tpl: 'piano', params: { humanize: '18', set: 'felt-piano' }, flags: [] };
const flat = { links: [{ from: 'chords', to: 'keys' }, { from: 'keys', to: 'mix' }] };

test('a block is reduced to its type, parameters with their documentation, and what it reads and feeds', () => {
  const s = blockSubject(node, flat, templates);
  assert.equal(s.title, 'Piano');
  assert.deepEqual(s.reads, ['chords']);
  assert.deepEqual(s.feeds, ['mix']);
  assert.equal(s.params.find((p) => p.name === 'humanize').doc, 'A little human feel for a sampled instrument.');
  assert.equal(subjectName(s), 'keys');
});

test('a field carries what the template says about it, and is explained without a model', () => {
  const s = fieldSubject(node, 'humanize', templates);
  assert.equal(subjectName(s), 'humanize of keys');
  const a = explainLocally(s, templates);
  assert.equal(a.title, 'Humanize');
  assert.match(a.text, /human feel/);
  assert.match(a.more, /It is 18 now/);
  assert.match(a.more, /from 0 to 100 %/);
  const other = explainLocally(fieldSubject(node, 'set', templates), templates);
  assert.match(other.more, /felt-piano/);
});

test('the documentation of a parameter is read from the field the server sends (documentation) as well as doc', () => {
  const real = { piano: { title: 'Piano', parameters: [{ name: 'humanize', label: 'Humanize', documentation: 'Real server text.', default: '0' }] } };
  assert.equal(fieldSubject(node, 'humanize', real).doc, 'Real server text.');
  assert.equal(explainLocally(fieldSubject(node, 'humanize', real), real).text, 'Real server text.');
  assert.equal(blockSubject(node, flat, real).params.find((p) => p.name === 'humanize').doc, 'Real server text.');
});

test('a block is explained from its type and its links', () => {
  const a = explainLocally(blockSubject(node, flat, templates), templates);
  assert.match(a.title, /keys is a Piano/);
  assert.match(a.text, /Plays the notes/);
  assert.match(a.text, /reads from chords/);
  assert.match(a.text, /feeds mix/);
  assert.match(a.more, /humanize \(18\)/);
});

test('a piece of code knows the block it sits in and its lines', () => {
  const text = 'composition "x" { ample 1  tempo 100 }\npiano keys at 1 for 4 {\n  gain -9\n  humanize 18\n}\n';
  const from = text.indexOf('humanize');
  const to = from + 'humanize 18'.length;
  const s = codeSubject(text, from, to);
  assert.deepEqual(s.block, { type: 'piano', name: 'keys', line: 2 });
  assert.equal(s.startLine, 4);
  assert.equal(subjectName(s), 'line 4');
  assert.match(explainLocally(s, templates).title, /in keys/);
  const none = codeSubject('hello world', 0, 5);
  assert.equal(none.block, null);
  assert.equal(explainLocally(none, templates), null);
});

test('what Koda is told names the subject and the question or the instruction', () => {
  const block = blockSubject(node, flat, templates);
  assert.match(promptOf(block, '', 'explain'), /block keys \(a piano\)/);
  assert.match(promptOf(block, '', 'explain'), /what does this do/);
  const field = promptOf(fieldSubject(node, 'humanize', templates), 'make it softer', 'run');
  assert.match(field, /field humanize \(now: 18\) of the block keys/);
  assert.match(field, /Instruction: make it softer/);
  const code = codeSubject('piano keys at 1 for 4 {\n  gain -9\n}\n', 30, 38);
  const asked = promptOf(code, 'why?', 'explain');
  assert.match(asked, /inside the block keys/);
  assert.match(asked, /Question: why\?/);
});

test('the changes of a task become a short list: added lines (links apart), and how many were taken out', () => {
  const diff = ['--- a/song', '+++ b/song', '@@', '+reverb room {', '+  from keys.audio -> in', '+}', '-old line', '-another', ''].join('\n');
  assert.deepEqual(summaryOf([{ diff }]), [
    { type: 'add', text: 'reverb room {' }, { type: 'link', text: 'from keys.audio -> in' }, { type: 'add', text: '}' }, { type: 'remove', text: '2 lines taken out' },
  ]);
  const many = Array.from({ length: 7 }, (_, i) => `+line ${i}`).join('\n');
  assert.equal(summaryOf([{ diff: many }]).at(-1).text, 'and 3 more lines');
});

test('a block type, a template and a file can be asked about too', async () => {
  const { typeSubject, templateSubject, fileSubject, subjectName, explainLocally, promptOf } = await import('../koda-context.mjs');
  const type = typeSubject({ id: 'reverb', title: 'Reverb', description: 'Adds space.', parameters: [{ name: 'room', documentation: 'The size of the room.' }, { name: 'mix' }] });
  assert.equal(subjectName(type), 'the Reverb block');
  assert.deepEqual(explainLocally(type, {}), { title: 'Reverb (block type)', text: 'Adds space.', more: 'room: The size of the room.' });
  assert.match(promptOf(type, '', 'explain'), /block type Reverb \(reverb\)/);
  const card = templateSubject({ name: 'Boom bap', say: 'Dusty and slow', meta: '90 bpm' });
  assert.equal(explainLocally(card, {}).text, 'Dusty and slow');
  assert.match(promptOf(card, 'make it busier', 'run'), /template "Boom bap": Dusty and slow/);
  const file = fileSubject('samples/kick.wav');
  assert.equal(subjectName(file), 'kick.wav');
  assert.equal(explainLocally(file, {}), null);
  assert.match(promptOf(file, '', 'explain'), /file samples\/kick\.wav/);
});

test('a closing remark that nothing was changed is left out of an answer', async () => {
  const { withoutNoChange, promptOf } = await import('../koda-context.mjs');
  assert.equal(withoutNoChange('It feeds the mixer. No changes made.'), 'It feeds the mixer.');
  assert.equal(withoutNoChange('It feeds the mixer. No files changed'), 'It feeds the mixer.');
  assert.equal(withoutNoChange('No changes were needed in this block, so the pan is fine.'), 'No changes were needed in this block, so the pan is fine.');
  assert.match(promptOf({ kind: 'block', block: 'a', type: 'b' }, '', 'explain'), /do not say that nothing was changed/);
});

test('what the model says about its own check is not shown as a tool name', async () => {
  const { splitCheck } = await import('../koda-context.mjs');
  assert.deepEqual(splitCheck('Removed the unused duck block. ample_check reports no new errors or warnings.'), { text: 'Removed the unused duck block.', check: 'ok' });
  assert.deepEqual(splitCheck('Added a reverb.'), { text: 'Added a reverb.', check: null });
  const bad = splitCheck('Added a reverb. ample_check found 2 new errors.');
  assert.equal(bad.check, 'issue');
  assert.equal(bad.text, 'Added a reverb. the AMPLE check found 2 new errors.');
});
