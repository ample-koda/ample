import test from 'node:test';
import assert from 'node:assert/strict';
import { addChannel, addClip, addPattern, addSection, barsOfRow, check, deletePattern, duplicatePattern, formatLanes, formatSeq, lanesRenamed, lanesWithout, parseLanes, parseSeq, removeChannel, removeClip, removeSection, renameChannel, renamePattern, setChannel, setPattern, setRow, songBars, toggleStep, trackCount, uniqueName, updateClip, updateSection } from '../seq-model.mjs';

const TEXT = 'sequencer 1\nchannel kick drum sample=samples/kick.wav vol=-3\nchannel lead notes wave=square\npattern verse bars=2 color=#4a90d9\n  kick x...x...|x.......\n  lead 1:0:1:60:96 2:0:2:67:96\nplaylist\n  clip verse track=1 at=1 for=4\n  section Intro at=1 for=2 color=#4a90d9\n';

test('a sequence reads and writes the same text, and a mistake names its line', () => {
  const { seq, error } = parseSeq(TEXT);
  assert.equal(error, undefined);
  assert.deepEqual(seq.channels.map((c) => [c.name, c.type]), [['kick', 'drum'], ['lead', 'notes']]);
  assert.equal(seq.channels[0].settings.vol, '-3');
  assert.deepEqual(seq.patterns[0].rows, { kick: 'x...x...|x.......', lead: '1:0:1:60:96 2:0:2:67:96' });
  assert.deepEqual(seq.clips[0], { pattern: 'verse', track: 1, at: 1, bars: 4 });
  assert.equal(formatSeq(seq), TEXT);
  assert.match(parseSeq('sequencer 1\nchannel kick drum\npattern p bars=0\n').error, /line 3/);
  for (const bad of ['channel kick drum', 'sequencer 1\nchannel kick wobble', 'sequencer 1\nchannel kick drum\nchannel kick drum', 'sequencer 1\n  indented', 'sequencer 1\nplaylist\n  clip x']) assert.ok(parseSeq(bad).error, bad);
  assert.equal(formatSeq(parseSeq('# a comment\nsequencer 1\n\nchannel a drum  sample="a b.wav"\n').seq), '# a comment\nsequencer 1\nchannel a drum sample="a b.wav"\n');   // blank lines and extra spaces go, comments stay
});

test('the lanes are read and written like the engine does', () => {
  const { seq } = parseSeq(TEXT);
  assert.deepEqual(parseLanes('lane1=kick lane2=kick+lead lane3=all', seq.channels), { 1: ['kick'], 2: ['kick', 'lead'], 3: ['kick', 'lead'] });
  assert.equal(formatLanes({ 2: ['lead'], 1: ['kick'], 3: [] }), 'lane1=kick lane2=lead');
  assert.deepEqual(parseLanes('lane0=kick lane9=kick junk', seq.channels), {});
  assert.deepEqual(lanesWithout({ 1: ['kick', 'lead'] }, 'kick'), { 1: ['lead'] });
  assert.deepEqual(lanesRenamed({ 1: ['kick'] }, 'kick', 'bd'), { 1: ['bd'] });
});

test('drum steps: a row is one group of sixteen steps a bar, a click cycles the step and writes every bar', () => {
  assert.deepEqual(barsOfRow('x...', 2), ['x...x...x...x...', 'x...x...x...x...']);
  assert.deepEqual(barsOfRow('x...x...x...x...|....x...', 2), ['x...x...x...x...', '....x.......x...']);
  const { seq } = parseSeq(TEXT);
  const next = toggleStep(seq, 'verse', 'kick', 0, 1);
  assert.equal(next.patterns[0].rows.kick.split('|')[0], 'xx..x...x...x...');
  assert.equal(seq.patterns[0].rows.kick, 'x...x...|x.......');   // the argument is untouched
  let cycled = seq;
  const seen = [];
  for (let i = 0; i < 4; i++) { cycled = toggleStep(cycled, 'verse', 'kick', 0, 1); seen.push(cycled.patterns[0].rows.kick[1]); }
  assert.deepEqual(seen, ['x', 'X', 'o', '.']);
});

test('channels: add with a free name, rename follows the rows, remove takes the rows away, settings are set and cleared', () => {
  const { seq } = parseSeq(TEXT);
  const added = addChannel(seq, 'kick', 'drum');
  assert.equal(added.name, 'kick-2');
  assert.equal(renameChannel(seq, 'kick', 'lead'), null);
  assert.equal(renameChannel(seq, 'kick', 'bad name'), null);
  const renamed = renameChannel(seq, 'kick', 'bd');
  assert.deepEqual(Object.keys(renamed.patterns[0].rows), ['bd', 'lead']);
  assert.deepEqual(Object.keys(removeChannel(seq, 'kick').patterns[0].rows), ['lead']);
  const set = setChannel(seq, 'kick', { vol: '-6', pan: '', mute: 'on', type: 'loop' });
  assert.deepEqual([set.channels[0].settings.vol, set.channels[0].settings.pan, set.channels[0].settings.mute, set.channels[0].type], ['-6', undefined, 'on', 'loop']);
});

test('patterns: add, duplicate, rename follows the clips, delete takes the clips, bars and colour', () => {
  const { seq } = parseSeq(TEXT);
  assert.equal(addPattern(seq, 'verse', 2).name, 'verse-2');
  assert.equal(duplicatePattern(seq, 'verse').name, 'verse-copy');
  assert.equal(renamePattern(seq, 'verse', 'chorus').clips[0].pattern, 'chorus');
  assert.equal(renamePattern(seq, 'verse', 'verse'), null);
  assert.deepEqual(deletePattern(seq, 'verse').clips, []);
  assert.equal(setPattern(seq, 'verse', { bars: 5000, color: '#ffffff' }).patterns[0].bars, 999);
  assert.equal(setPattern(seq, 'verse', { color: '#ffffff' }).patterns[0].color, '#ffffff');
  assert.equal(setRow(seq, 'verse', 'kick', '').patterns[0].rows.kick, undefined);
});

test('the playlist: clips are added, moved, resized and removed, sections edited, and a clip of a missing pattern is a problem', () => {
  const { seq } = parseSeq(TEXT);
  let s = addClip(seq, 'verse', 5, 2, 2);
  assert.deepEqual(s.clips[1], { pattern: 'verse', track: 2, at: 5, bars: 2 });
  assert.equal(songBars(s), 6);
  assert.equal(trackCount(s), 4);
  s = updateClip(s, 1, { at: 0, bars: 0 });
  assert.deepEqual([s.clips[1].at, s.clips[1].bars], [1, 1]);
  assert.equal(removeClip(s, 0).clips.length, 1);
  s = addSection(s, 'Big Chorus', 5, 8, '#fff');
  assert.equal(s.sections[1].name, 'Big-Chorus');
  assert.equal(updateSection(s, 1, { at: 9 }).sections[1].at, 9);
  assert.equal(removeSection(s, 0).sections.length, 1);
  assert.deepEqual(check(addClip(seq, 'nope', 1, 1)), ["a clip names the pattern 'nope', which is not defined"]);
  assert.deepEqual(check(setRow(seq, 'verse', 'ghost', 'x')), ["the pattern 'verse' has a row for 'ghost', which is not a channel"]);
  assert.equal(uniqueName('  ', ['item']), 'item-2');
});

const COMMENTED = [
  '# the band of the song',
  'sequencer 1',
  '# the kick',
  'channel kick drum sample=samples/kick.wav',
  '# the verse, four bars',
  'pattern verse bars=4',
  '  # on every beat',
  '  kick x...x...x...x...',
  '# when it plays',
  'playlist',
  '  # the first verse',
  '  clip verse track=1 at=1 for=4',
  '  # the first part',
  '  section Verse at=1 for=4',
  '  # the drop',
  '  marker Drop at=3',
  '  # keep going',
  '  loop at=1 for=4',
  '# the end',
  '',
].join('\n');

test('comments stay where they were written: above their line, at the top and at the end', () => {
  const { seq, error } = parseSeq(COMMENTED);
  assert.equal(error, undefined);
  assert.deepEqual(seq.intro, ['# the band of the song']);
  assert.deepEqual(seq.channels[0].notes, ['# the kick']);
  assert.deepEqual(seq.patterns[0].notes, ['# the verse, four bars']);
  assert.deepEqual(seq.patterns[0].rowNotes, { kick: ['# on every beat'] });
  assert.deepEqual(seq.playlistNotes, ['# when it plays']);
  assert.deepEqual(seq.clips[0].notes, ['# the first verse']);
  assert.deepEqual(seq.footer, ['# the end']);
  assert.equal(formatSeq(seq), COMMENTED);
});

test('edits keep the comments of what they do not touch, and a comment leaves with its line', () => {
  const { seq } = parseSeq(COMMENTED);
  assert.match(formatSeq(setPattern(seq, 'verse', { bars: 8 })), /# the verse, four bars\npattern verse bars=8/);
  assert.match(formatSeq(addChannel(seq, 'hat', 'drum').seq), /# the kick\nchannel kick/);
  assert.match(formatSeq(renameChannel(seq, 'kick', 'bd')), /  # on every beat\n  bd x\.\.\./);
  assert.doesNotMatch(formatSeq(setRow(seq, 'verse', 'kick', '')), /on every beat/);
  assert.doesNotMatch(formatSeq(removeChannel(seq, 'kick')), /# the kick|on every beat/);
  assert.equal(formatSeq(removeClip(seq, 0)).includes('# the first verse'), false);
  assert.equal(formatSeq(duplicatePattern(seq, 'verse').seq).split('# the verse, four bars').length, 2);   // the copy has no comment of its own
});

test('editing one lane keeps `all` in the other lanes, also when a channel is renamed or removed', async () => {
  const { parseLanesRaw, toggleLaneChannel } = await import('../seq-model.mjs');
  const channels = [{ name: 'kick' }, { name: 'hat' }, { name: 'lead' }];
  const text = '"lane1=all lane2=hat"';
  assert.deepEqual(parseLanesRaw(text), { 1: ['all'], 2: ['hat'] });
  assert.equal(formatLanes(parseLanesRaw(text)), 'lane1=all lane2=hat');
  // add a channel to lane 2: lane 1 stays `all`
  assert.equal(formatLanes(toggleLaneChannel(text, channels, 2, 'lead')), 'lane1=all lane2=hat+lead');
  // take a channel out of lane 2
  assert.equal(formatLanes(toggleLaneChannel(text, channels, 2, 'hat')), 'lane1=all');
  // taking a channel out of the `all` lane itself writes that lane out as names, and only that lane
  assert.equal(formatLanes(toggleLaneChannel(text, channels, 1, 'hat')), 'lane1=kick+lead lane2=hat');
  // the old behaviour for contrast: a parse with the real channels expanded every lane
  assert.equal(formatLanes(parseLanes(text, channels)), 'lane1=kick+hat+lead lane2=hat');
  // rename and remove work on the raw lanes and leave `all` alone
  assert.equal(formatLanes(lanesRenamed(parseLanesRaw(text), 'hat', 'hh')), 'lane1=all lane2=hh');
  assert.equal(formatLanes(lanesWithout(parseLanesRaw(text), 'hat')), 'lane1=all');
  // a lane that mixes `all` with names is `all`
  assert.deepEqual(parseLanesRaw('lane1=kick+all'), { 1: ['all'] });
});
