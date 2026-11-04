import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { FX_KINDS, activity, fxKind, inParts, knownTypes, levelAt, linkKey, localBar, secondsPerBar, songBar, takeTime } from '../block-activity.mjs';

const node = (id, tpl, parts = [], extra = {}) => ({ id, tpl, parts, flags: [], disabledBy: null, ...extra });
const link = (from, to, type = 'audio', extra = {}) => ({ from, out: 0, to, inp: 0, type, ...extra });

test('bar maths: 120 bpm is 2 seconds a bar and a take that starts at bar 9 begins there', () => {
  assert.equal(secondsPerBar(120), 2);
  assert.equal(secondsPerBar(0), 2);
  assert.equal(songBar(0, 120), 1);
  assert.equal(songBar(4, 120), 3);
  assert.equal(songBar(4, 120, 9), 11);
  assert.equal(songBar(-3, 120), 1);
});

test('parts cover a bar from their start up to, not including, their end; no parts means the whole song', () => {
  const parts = [{ at: 5, length: 4 }];
  assert.equal(inParts(parts, 4.99), false);
  assert.equal(inParts(parts, 5), true);
  assert.equal(inParts(parts, 8.99), true);
  assert.equal(inParts(parts, 9), false);
  assert.equal(inParts([], 100), true);
  assert.equal(inParts(undefined, 1), true);
});

const song = {
  nodes: [
    node('beat', 'midi-clip', [{ at: 1, length: 8 }]),
    node('kit', 'drums', [{ at: 1, length: 8 }]),
    node('verb', 'reverb'),
    node('keys', 'piano', [{ at: 5, length: 4 }]),
    node('mix', 'mixer'),
    node('master', 'master'),
    node('lonely', 'synth'),
  ],
  links: [link('beat', 'kit', 'midi'), link('kit', 'verb'), link('verb', 'mix'), link('keys', 'mix'), link('mix', 'master')],
};

test('a source lights inside its parts and the chain to the master lights with it', () => {
  const a = activity(song, 2);
  assert.deepEqual([...a.nodes].sort(), ['beat', 'kit', 'master', 'mix', 'verb']);
  assert.ok(a.links.has(linkKey(link('beat', 'kit'))));
  assert.ok(a.links.has(linkKey(link('mix', 'master'))));
  assert.ok(!a.links.has(linkKey(link('keys', 'mix'))));
});

test('a second source joins at its own bar, and everything goes dark after the last part', () => {
  assert.ok(activity(song, 6).nodes.has('keys'));
  assert.ok(activity(song, 6).links.has(linkKey(link('keys', 'mix'))));
  const after = activity({ ...song, nodes: song.nodes.map((n) => (n.id === 'verb' ? { ...n, parts: [{ at: 1, length: 8 }] } : n)) }, 20);
  assert.equal(after.nodes.size, 0);   // every source is past its parts, so nothing downstream is fed
});

test('a clip without parts is there only while the instrument it feeds is there', () => {
  const g = {
    nodes: [node('clip', 'midi-clip'), node('lead', 'synth', [{ at: 3, length: 2 }]), node('master', 'master')],
    links: [link('clip', 'lead', 'midi'), link('lead', 'master')],
  };
  assert.deepEqual([...activity(g, 1).nodes], []);
  assert.deepEqual([...activity(g, 3.5).nodes].sort(), ['clip', 'lead', 'master']);
  assert.deepEqual([...activity(g, 5).nodes], []);
});

test('a block whose sound goes nowhere (no path to the master) is not lit', () => {
  assert.ok(!activity(song, 2).nodes.has('lonely'));
});

test('disabled links, disabled blocks and text or data links never light', () => {
  const cut = { nodes: song.nodes, links: song.links.map((l) => (l.from === 'kit' ? { ...l, disabled: true } : l)) };
  assert.ok(!activity(cut, 2).nodes.has('verb'));
  const off = { nodes: song.nodes.map((n) => (n.id === 'verb' ? { ...n, disabledBy: 'kit' } : n)), links: song.links };
  assert.ok(!activity(off, 2).nodes.has('verb'));
  const text = { nodes: [node('words', 'lyrics'), node('voice', 'ai-voice'), node('master', 'master')], links: [link('words', 'voice', 'text'), link('voice', 'master')] };
  const a = activity(text, 1);
  assert.ok(a.nodes.has('voice'));
  assert.ok(!a.links.has(linkKey(link('words', 'voice', 'text'))));
});

test('a song without a master still lights its chain, and a cycle does not hang', () => {
  const noMaster = { nodes: [node('a', 'synth'), node('b', 'filter')], links: [link('a', 'b')] };
  assert.deepEqual([...activity(noMaster, 1).nodes].sort(), ['a', 'b']);
  const loop = { nodes: [node('a', 'delay'), node('b', 'filter')], links: [link('a', 'b'), link('b', 'a')] };
  assert.equal(activity(loop, 1).nodes.size, 0);   // nothing feeds the loop from outside
});

test('a mixer is lit as soon as any one of its inputs is lit', () => {
  const g = { nodes: [node('x', 'synth', [{ at: 1, length: 1 }]), node('y', 'bass', [{ at: 3, length: 1 }]), node('mix', 'mixer'), node('master', 'master')], links: [link('x', 'mix'), link('y', 'mix'), link('mix', 'master')] };
  assert.deepEqual([...activity(g, 1.5).nodes].sort(), ['master', 'mix', 'x']);
  assert.deepEqual([...activity(g, 3.5).nodes].sort(), ['master', 'mix', 'y']);
  assert.deepEqual([...activity(g, 2.5).nodes].sort(), []);
});

test('the level of a take at a bar reads the peaks, looks one point around it, and is 0 outside the take', () => {
  const entry = { seconds: 8, peaks: [0, 0, -0.5, 0.25, 0, 0, 0, 0] };   // 4 points of 2 seconds each = 1 bar each at 120 bpm
  assert.equal(levelAt(entry, 1, 120), 0.5);      // point 0 looks at point 1 too
  assert.equal(levelAt(entry, 2.5, 120), 0.5);    // point 1
  assert.equal(levelAt(entry, 4.9, 120), 0);      // point 3: silence all around it
  assert.equal(levelAt(entry, 5, 120), 0);        // past the end of the take
  assert.equal(levelAt(entry, 2, 120, 3), 0);     // before a take that starts at bar 3
  assert.equal(levelAt(null, 1, 120), null);
  assert.equal(levelAt({ seconds: 0, peaks: [1, 1] }, 1, 120), null);
});

test('a block take holds only the bars of its own parts, one after the other; otherwise it is a stretch of the song from its first bar', () => {
  const parts = [{ at: 3, length: 2 }];
  const local = { seconds: 4, peaks: [0, 0, 0, 0, 0.9, 0.9, 0.9, 0.9] };   // 2 bars at 120 bpm = 4 seconds, 4 points of 1 second
  assert.equal(takeTime(local, 3.5, 120, 1, parts), 1);
  assert.equal(takeTime(local, 4, 120, 1, parts), 2);
  assert.equal(takeTime(local, 5, 120, 1, parts), -1);       // bar 5 is not in its parts
  assert.equal(levelAt(local, 3.2, 120, 1, 0.1, parts), 0);
  assert.equal(levelAt(local, 4.2, 120, 1, 0.1, parts), 0.9);
  assert.equal(levelAt(local, 6, 120, 1, 0.5, parts), 0);
  const two = [{ at: 5, length: 1 }, { at: 9, length: 1 }];   // two parts: the second one follows the first in the take
  assert.equal(takeTime({ seconds: 4 }, 9.5, 120, 1, two), 3);
  const stretch = { seconds: 8 };   // as long as 4 bars, the parts are 2: not the local kind, so it starts at its first bar
  assert.equal(takeTime(stretch, 5, 120, 3, parts), 4);
});

test('the bar of a block counts from its own first bar, through its parts one after the other', () => {
  assert.equal(localBar([], 5.5), 4);
  assert.equal(localBar([{ at: 3, length: 2 }], 3.2), 0);
  assert.equal(localBar([{ at: 3, length: 2 }], 4.9), 1);
  assert.equal(localBar([{ at: 3, length: 2 }], 5), -1);
  assert.equal(localBar([{ at: 9, length: 2 }, { at: 3, length: 2 }], 9.5), 2);
});

test('every block type of the templates has a visual character, and every character is a known one', () => {
  const dir = new URL('../../../spec/templates/', import.meta.url);
  const names = fs.readdirSync(dir).filter((f) => f.endsWith('.json')).map((f) => f.slice(0, -5));
  assert.ok(names.length > 50);
  const missing = names.filter((n) => !knownTypes().includes(n));
  assert.deepEqual(missing, []);
  for (const type of knownTypes()) assert.ok(FX_KINDS.includes(fxKind(type)), type);
  assert.equal(fxKind('drums'), 'pulse');
  assert.equal(fxKind('reverb'), 'echo');
  assert.equal(fxKind('something-new'), 'wave');
});
