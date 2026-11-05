import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { derivePlaylist, kindOf, secondsPerBar } from '../playlist-model.mjs';

// the real templates of the spec: the model must work with the real port names
const dir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'spec', 'templates');
const templates = Object.fromEntries(fs.readdirSync(dir).filter((f) => f.endsWith('.json')).map((f) => { const j = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')); return [j.id, j]; }));

// a tiny song builder: b(name, type, { from: [[block, output, input], ...], parts, params, flags })
const b = (name, type, o = {}) => ({ name, type, froms: (o.from || []).map(([block, output, input, disabled]) => ({ block, output, input, disabled: !!disabled })), parts: o.parts || [], parameters: Object.entries(o.params || {}).map(([n, value]) => ({ name: n, value })), flags: o.flags || [] });
const derive = (blocks, view, options, uses) => derivePlaylist({ blocks, uses }, templates, view, options);
const names = (line) => ({ chain: line.chain, sources: line.sources.map((s) => s.block) });
const everyBlock = (blocks, r) => {
  const seen = new Map();
  const add = (n) => seen.set(n, (seen.get(n) || 0) + 1);
  for (const line of r.lines) { line.chain.forEach(add); line.sources.forEach((s) => s.block && add(s.block)); line.noteSides.forEach((n) => n.steps.forEach((s) => { if (!seen.has(s)) add(s); })); line.lanes.forEach(add); }
  r.tail.forEach((t) => add(t.block));
  r.terminals.forEach((t) => add(t));
  r.loose.forEach((l) => add(l.block));
  return seen;
};

const basic = () => [
  b('chords', 'midi-clip', { parts: [{ at: 1, length: 8, name: '' }] }),
  b('pad', 'synth', { from: [['chords', 'notes', 'notes']], parts: [{ at: 1, length: 8, name: 'verse' }] }),
  b('pad-room', 'reverb', { from: [['pad', 'audio', 'audio']] }),
  b('pad-bus', 'bus', { from: [['pad-room', 'audio', 'audio1']] }),
  b('beat', 'drums', { parts: [{ at: 1, length: 8, name: '' }, { at: 9, length: 8, name: '' }] }),
  b('beat-bus', 'bus', { from: [['beat', 'audio', 'audio1']] }),
  b('mix', 'bus', { from: [['pad-bus', 'audio', 'audio1'], ['beat-bus', 'audio', 'audio2']] }),
  b('final', 'volume', { from: [['mix', 'audio', 'audio']] }),
  b('out', 'master', { from: [['final', 'audio', 'audio']] }),
];

test('block kinds come from the real templates', () => {
  const k = (t) => kindOf(t, templates[t]);
  assert.deepEqual(['synth', 'drums', 'sampler', 'gm', 'import-audio'].map(k), ['source', 'source', 'source', 'source', 'source']);
  assert.deepEqual(['reverb', 'compressor', 'volume', 'eq'].map(k), ['chain', 'chain', 'chain', 'chain']);
  assert.deepEqual(['bus', 'mixer', 'send', 'join'].map(k), ['merge', 'merge', 'merge', 'merge']);
  assert.deepEqual(['midi-clip', 'arpeggiator', 'curve', 'lyrics', 'master'].map(k), ['noteSource', 'noteStep', 'control', 'data', 'tail']);
  assert.equal(kindOf('nothing-like-it', undefined), 'unknown');
});

test('every template has a kind, so a new block type cannot land in loose blocks unnoticed', () => {
  const unknown = Object.keys(templates).filter((t) => kindOf(t, templates[t]) === 'unknown');
  assert.deepEqual(unknown.sort(), []);
});

test('a plain song: two lines under one bus, the tail after it, clips from the parts', () => {
  const r = derive(basic());
  assert.deepEqual(r.terminals, ['mix']);
  assert.deepEqual(r.tail.map((t) => t.block), ['final', 'out']);
  assert.equal(r.lines.length, 2);
  const [pad, beat] = r.lines;
  assert.equal(pad.name, 'pad');
  assert.deepEqual(names(pad), { chain: ['pad-room', 'pad-bus'], sources: ['pad'] });
  assert.deepEqual(pad.noteSides, [{ instrument: 'pad', steps: ['chords'] }]);
  assert.deepEqual(pad.clips.map((c) => [c.block, c.at, c.length]), [['pad', 1, 8]]);
  assert.equal(beat.name, 'beat');
  assert.deepEqual(beat.clips.map((c) => [c.at, c.length]), [[1, 8], [9, 8]]);
  assert.deepEqual(r.loose, []);
});

test('nothing is ever dropped: every block appears exactly once', () => {
  const song = [...basic(), b('stray', 'reverb'), b('lfo', 'curve', { }), b('words', 'lyrics')];
  const r = derive(song);
  const seen = everyBlock(song, r);
  for (const block of song) assert.equal(seen.get(block.name), 1, `${block.name} appears ${seen.get(block.name) || 0} times`);
  assert.ok(r.loose.some((l) => l.block === 'stray' && /not connected/.test(l.reason)));
});

test('two sources that play after each other into one bus are two clips on one line', () => {
  const song = [
    b('kick-0', 'sampler', { parts: [{ at: 1, length: 88, name: '' }] }),
    b('kick-1', 'sampler', { parts: [{ at: 89, length: 88, name: '' }] }),
    b('kick-bus', 'bus', { from: [['kick-0', 'audio', 'audio1'], ['kick-1', 'audio', 'audio2']] }),
    b('mix', 'bus', { from: [['kick-bus', 'audio', 'audio1'], ['x', 'audio', 'audio2']] }),
    b('x', 'synth', { parts: [{ at: 1, length: 4, name: '' }] }),
  ];
  const r = derive(song);
  const kick = r.lines.find((l) => l.name === 'kick');
  assert.deepEqual(kick.sources.map((s) => s.block), ['kick-0', 'kick-1']);
  assert.deepEqual(kick.clips.map((c) => [c.block, c.at, c.length]), [['kick-0', 1, 88], ['kick-1', 89, 88]]);
});

test('a sidechain key is a link between lines and not part of the chain', () => {
  const song = [
    b('kick', 'sampler', { parts: [{ at: 1, length: 8, name: '' }] }),
    b('kick-bus', 'bus', { from: [['kick', 'audio', 'audio1']] }),
    b('bass', 'synth', { parts: [{ at: 1, length: 8, name: '' }] }),
    b('bass-pump', 'compressor', { from: [['bass', 'audio', 'audio'], ['kick-bus', 'audio', 'key']] }),
    b('bass-bus', 'bus', { from: [['bass-pump', 'audio', 'audio1']] }),
    b('mix', 'bus', { from: [['kick-bus', 'audio', 'audio1'], ['bass-bus', 'audio', 'audio2']] }),
  ];
  const r = derive(song);
  const bass = r.lines.find((l) => l.name === 'bass');
  assert.deepEqual(bass.chain, ['bass-pump', 'bass-bus']);
  assert.equal(r.links.keys.length, 1);
  assert.deepEqual([r.links.keys[0].fromBlock, r.links.keys[0].toBlock], ['kick-bus', 'bass-pump']);
  assert.equal(r.links.keys[0].fromLine, r.lines.find((l) => l.name === 'kick').id);
  assert.equal(r.links.keys[0].toLine, bass.id);
});

test('a block that feeds two buses keeps one main route; the other is a send link', () => {
  const song = [
    b('lead', 'synth', { parts: [{ at: 1, length: 8, name: '' }] }),
    b('lead-bus', 'bus', { from: [['lead', 'audio', 'audio1']] }),
    b('hall', 'reverb', { from: [['lead', 'audio', 'audio']] }),
    b('hall-bus', 'bus', { from: [['hall', 'audio', 'audio1']] }),
    b('mix', 'bus', { from: [['lead-bus', 'audio', 'audio1'], ['hall-bus', 'audio', 'audio2']] }),
  ];
  const r = derive(song);
  assert.equal(r.lines.length, 2);
  assert.ok(r.links.sends.some((s) => s.from === 'lead' || s.to === 'hall'), JSON.stringify(r.links.sends));
  const seen = everyBlock(song, r);
  for (const block of song) assert.equal(seen.get(block.name), 1, block.name);
});

test('a note source feeding two instruments shows on both lines and is marked shared', () => {
  const song = [
    b('riff', 'midi-clip', { parts: [{ at: 1, length: 4, name: '' }] }),
    b('a', 'synth', { from: [['riff', 'notes', 'notes']], parts: [{ at: 1, length: 4, name: '' }] }),
    b('b', 'piano', { from: [['riff', 'notes', 'notes']], parts: [{ at: 1, length: 4, name: '' }] }),
    b('a-bus', 'bus', { from: [['a', 'audio', 'audio1']] }),
    b('b-bus', 'bus', { from: [['b', 'audio', 'audio1']] }),
    b('mix', 'bus', { from: [['a-bus', 'audio', 'audio1'], ['b-bus', 'audio', 'audio2']] }),
  ];
  const r = derive(song);
  assert.deepEqual(r.sharedNotes, ['riff']);
  assert.ok(r.lines.every((l) => l.noteSides.some((n) => n.steps.includes('riff'))));
  assert.ok(r.lines.every((l) => l.sharedNotes.length === 1));
});

test('control and data blocks are lanes of the line they steer', () => {
  const song = [...basic(), b('wobble', 'curve'), b('words', 'lyrics')];
  song.find((x) => x.name === 'pad-room').froms.push({ block: 'wobble', output: 'level', input: 'mix', disabled: false });
  const r = derive(song);
  const pad = r.lines.find((l) => l.name === 'pad');
  assert.ok(pad.lanes.includes('wobble'), JSON.stringify(r.lines.map((l) => l.lanes)));
  assert.ok(!r.loose.some((l) => l.block === 'wobble'));
});

test('a part with an unknown length is an auto clip, and a block without parts spans the song', () => {
  const song = [
    b('a', 'synth', { parts: [{ at: 1, length: 'auto', name: '' }] }),
    b('c', 'synth', { params: { bars: '6' } }),
    b('a-bus', 'bus', { from: [['a', 'audio', 'audio1']] }),
    b('c-bus', 'bus', { from: [['c', 'audio', 'audio1']] }),
    b('mix', 'bus', { from: [['a-bus', 'audio', 'audio1'], ['c-bus', 'audio', 'audio2']] }),
  ];
  const r = derive(song, {}, { songBars: 16 });
  assert.deepEqual(r.lines.find((l) => l.name === 'a').clips.map((c) => [c.auto, c.length]), [[true, null]]);
  assert.deepEqual(r.lines.find((l) => l.name === 'c').clips.map((c) => [c.implicit, c.length]), [[true, 6]]);
});

test('a disabled connection is not followed but reported, and a block that reaches nothing is loose with a reason', () => {
  const song = basic();
  song.find((x) => x.name === 'beat-bus').froms[0].disabled = true;
  const r = derive(song);
  assert.equal(r.lines.length, 2);   // the bus of the beat is still a line, an empty one
  assert.deepEqual(r.lines.find((l) => l.name === 'beat').sources, []);
  assert.deepEqual(r.links.disabled.map((d) => [d.from, d.to]), [['beat', 'beat-bus']]);
  assert.ok(r.loose.some((l) => l.block === 'beat'));
});

test('a second main input (crossfade) is a side link', () => {
  const song = [
    b('a', 'synth', { parts: [{ at: 1, length: 4, name: '' }] }),
    b('b', 'synth', { parts: [{ at: 1, length: 4, name: '' }] }),
    b('x', 'crossfade', { from: [['a', 'audio', 'a'], ['b', 'audio', 'b']] }),
    b('x-bus', 'bus', { from: [['x', 'audio', 'audio1']] }),
    b('other', 'synth', { parts: [{ at: 1, length: 4, name: '' }] }),
    b('other-bus', 'bus', { from: [['other', 'audio', 'audio1']] }),
    b('mix', 'bus', { from: [['x-bus', 'audio', 'audio1'], ['other-bus', 'audio', 'audio2']] }),
  ];
  const r = derive(song);
  assert.ok(r.links.sides.some((s) => s.from === 'b' && s.to === 'x'));
  assert.ok(everyBlock(song, r).get('b') >= 1);
});

test('a module instance is one line step', () => {
  const uses = [{ instance: 'groove', froms: [], inputs: [], outputs: [{ name: 'audio', type: 'audio' }], parameters: [] }];
  const song = [b('groove-bus', 'bus', { from: [['groove', 'audio', 'audio1']] }), b('s', 'synth', { parts: [{ at: 1, length: 4, name: '' }] }), b('s-bus', 'bus', { from: [['s', 'audio', 'audio1']] }), b('mix', 'bus', { from: [['groove-bus', 'audio', 'audio1'], ['s-bus', 'audio', 'audio2']] })];
  const r = derive(song, {}, {}, uses);
  assert.ok(r.lines.some((l) => l.sources.some((s) => s.block === 'groove')));
});

test('names, order and groups of the view win over the computed ones', () => {
  const view = { lineNames: { pad: 'Pad stack' }, order: ['beat-bus', 'pad-bus'], groups: { g: { id: 'g', title: 'Musical', members: ['pad-bus'] } } };
  const r = derive(basic(), view);
  assert.deepEqual(r.lines.map((l) => l.name), ['beat', 'Pad stack']);
  assert.equal(r.lines[1].customName, true);
  const musical = r.groups.find((g) => g.title === 'Musical');
  assert.deepEqual(musical.lines, [r.lines[1].id]);
  assert.equal(musical.custom, true);
});

test('the default groups: the type of the sources when two lines share it, else a prefix, else Lines', () => {
  const song = [
    b('s1', 'sampler', { parts: [{ at: 1, length: 4, name: '' }] }), b('s1-bus', 'bus', { from: [['s1', 'audio', 'audio1']] }),
    b('s2', 'sampler', { parts: [{ at: 1, length: 4, name: '' }] }), b('s2-bus', 'bus', { from: [['s2', 'audio', 'audio1']] }),
    b('lead', 'synth', { parts: [{ at: 1, length: 4, name: '' }] }), b('lead-bus', 'bus', { from: [['lead', 'audio', 'audio1']] }),
    b('mix', 'bus', { from: [['s1-bus', 'audio', 'audio1'], ['s2-bus', 'audio', 'audio2'], ['lead-bus', 'audio', 'audio3']] }),
  ];
  const r = derive(song);
  assert.deepEqual(r.groups.map((g) => [g.title, g.lines.length]).sort(), [['Lines', 1], ['Samples', 2]]);
});

test('the status of a line is what its blocks say together', () => {
  const statusOf = (name) => ({ pad: 'built', 'pad-room': 'failed', 'pad-bus': 'built', beat: 'built', 'beat-bus': 'built' }[name]);
  const r = derive(basic(), {}, { statusOf });
  assert.equal(r.lines.find((l) => l.name === 'pad').status, 'failed');
  assert.equal(r.lines.find((l) => l.name === 'beat').status, 'built');
});

test('seconds per bar follow the meter, not a constant 4', () => {
  assert.equal(secondsPerBar(120, 4), 2);
  assert.equal(secondsPerBar(120, 3), 1.5);
  assert.equal(secondsPerBar(120, 7), 3.5);
  assert.equal(derive(basic(), {}, { beatsPerBar: 3 }).secondsPerBar(120), 1.5);
});

test('the same input always gives the same order', () => {
  const a = JSON.stringify(derive(basic()).lines.map((l) => l.id + l.name));
  const b2 = JSON.stringify(derive(basic().reverse()).lines.map((l) => l.id + l.name));
  assert.equal(a, b2.replace(/line-\d+/g, (m) => m));
  assert.deepEqual(derive(basic()).lines.map((l) => l.name), derive(basic().reverse()).lines.map((l) => l.name));
});

test('the module has no DOM and no imports from web/app', () => {
  const source = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'playlist-model.mjs'), 'utf8');
  assert.equal(/^\s*import\s/m.test(source), false);
  assert.equal(/document\.|window\.|fetch\(/.test(source.replace(/\/\/.*$/gm, '')), false);
});
