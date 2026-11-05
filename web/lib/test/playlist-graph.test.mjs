import test from 'node:test';
import assert from 'node:assert/strict';
import { lineGraph } from '../playlist-graph.mjs';
import { blockLines, laneClips, parseSequencer } from '../sequencer-lanes.mjs';

const line = {
  chain: ['pad-room', 'pad-bus'],
  sources: [{ block: 'pad', pre: [] }],
  noteSides: [{ instrument: 'pad', steps: ['chords'] }],
  lanes: [],
};

test('the graph of a line runs from the notes through the source and the chain to the bus', () => {
  const g = lineGraph(line);
  assert.deepEqual(g.nodes.map((n) => [n.id, n.kind]), [['chords', 'note'], ['pad', 'source'], ['pad-room', 'chain'], ['pad-bus', 'chain']]);
  assert.deepEqual(g.edges.map((e) => `${e.from}>${e.to}`), ['chords>pad', 'pad>pad-room', 'pad-room>pad-bus']);
  assert.ok(g.nodes.every((n, i, a) => i === 0 || n.x > a[i - 1].x), 'one column after the other');
  assert.ok(g.width > 0 && g.height > 0);
});

test('a sidechain key is a dashed edge from a node above the block it ducks', () => {
  const g = lineGraph({ ...line, noteSides: [] }, [{ fromBlock: 'kick-bus', toBlock: 'pad-room' }]);
  const key = g.nodes.find((n) => n.kind === 'key');
  assert.equal(key.block, 'kick-bus');
  assert.ok(g.edges.some((e) => e.dashed && e.from === key.id && e.to === 'pad-room'));
  assert.ok(key.y < g.nodes.find((n) => n.id === 'pad-room').y || g.nodes.find((n) => n.id === 'pad-room').y > 0);
});

test('two sources on one line both lead into its chain', () => {
  const g = lineGraph({ chain: ['kick-bus'], sources: [{ block: 'kick-0', pre: [] }, { block: 'kick-1', pre: [] }], noteSides: [], lanes: [] });
  assert.deepEqual(g.edges.map((e) => `${e.from}>${e.to}`), ['kick-0>kick-bus', 'kick-1>kick-bus']);
});

const sequencer = `sequencer performance pinned@1.0 at 5 for 40 {
  lanes lane1=kick lane2=hat+open lane3=lead
  sequence {
    sequencer 1
    channel kick drum sample=a.wav
    pattern pulse bars=4
      kick x...x...
      hat ..x...x.
    pattern theme bars=8
      lead 1:0:0.36:66:91 1:0.5:0.36:69:77
    playlist
      clip pulse track=1 at=1 for=8
      clip theme track=3 at=9 for=16
      section Ignition at=1 for=8
  }
}
filter next {
}`;

test('the lanes of a sequencer come from its text: channels per lane, patterns, playlist clips', () => {
  const lines = blockLines(sequencer, 0);
  assert.equal(lines.at(-1), '}');
  assert.ok(!lines.join('\n').includes('filter next'));
  const parsed = parseSequencer(lines);
  assert.deepEqual(parsed.lanes, [{ id: 'lane1', channels: ['kick'] }, { id: 'lane2', channels: ['hat', 'open'] }, { id: 'lane3', channels: ['lead'] }]);
  assert.deepEqual(parsed.patterns.pulse.channels, ['kick', 'hat']);
  assert.equal(parsed.patterns.theme.bars, 8);
  assert.deepEqual(parsed.sections, [{ name: 'Ignition', at: 1, length: 8 }]);
  const lanes = laneClips(parsed, 5);
  assert.deepEqual(lanes.map((l) => [l.id, l.clips.map((c) => [c.pattern, c.at, c.length])]), [['lane1', [['pulse', 5, 8]]], ['lane2', [['pulse', 5, 8]]], ['lane3', [['theme', 13, 16]]]]);
});

import { editPlaylist, rewriteClipLine } from '../sequencer-lanes.mjs';
import { addableParams, automatedParams, curvePath, flatY } from '../playlist-automation.mjs';

test('a clip line is rewritten in place, taken out, or added after the last clip', () => {
  const lines = blockLines(sequencer, 0);
  const parsed = parseSequencer(lines);
  assert.deepEqual(parsed.clips.map((c) => c.row), [parsed.clips[0].row, parsed.clips[0].row + 1]);
  assert.equal(rewriteClipLine('      clip pulse track=1 at=1 for=8', { at: 5, length: 12 }), '      clip pulse track=1 at=5 for=12');
  const moved = editPlaylist(lines, parsed, { move: { row: parsed.clips[0].row, at: 3, length: 4 } });
  assert.ok(moved.includes('      clip pulse track=1 at=3 for=4'));
  assert.equal(moved.length, lines.length);
  const removed = editPlaylist(lines, parsed, { remove: parsed.clips[1].row });
  assert.equal(removed.length, lines.length - 1);
  assert.ok(!removed.join('\n').includes('clip theme'));
  const added = editPlaylist(lines, parsed, { add: { pattern: 'pulse', track: 2, at: 25, length: 8 } });
  const row = added.indexOf('      clip pulse track=2 at=25 for=8');
  assert.equal(row, parsed.clips[1].row + 1, 'right after the last clip');
  assert.equal(parseSequencer(added).clips.length, 3);
  assert.equal(editPlaylist(['sequencer x {', '}'], parseSequencer(['sequencer x {', '}']), { add: { pattern: 'p', track: 1, at: 1, length: 1 } }), null, 'no playlist to add to');
});

test('the parameters of a block that carry an automation, the ones that can get one, and the path of a curve', () => {
  const node = { tpl: 'filter', params: { cutoff: 'auto(1:1800, 33:3800)', resonance: '8 %', type: 'lowpass' } };
  const [cutoff] = automatedParams(node);
  assert.deepEqual([cutoff.name, cutoff.kind, cutoff.points.length], ['cutoff', 'auto', 2]);
  assert.deepEqual(addableParams(node).map((p) => p.name), ['resonance']);
  const d = curvePath(cutoff.points, cutoff.range, 10, 400, 30);
  assert.match(d, /^M0,[\d.]+ L0\.0,[\d.]+ L320\.0,[\d.]+ L400,[\d.]+$/);
  assert.equal(curvePath(null, cutoff.range, 10, 400, 30), null);
  assert.ok(flatY('8 %', { min: 0, max: 100 }, 30) > flatY('80 %', { min: 0, max: 100 }, 30), 'a higher value is higher up');
  assert.equal(flatY('lfo(1/4)', { min: 0, max: 100 }, 30), null);
  assert.deepEqual(automatedParams({ tpl: 'lfo-less', params: { a: 'lfo(1/2, 1 oct)' } }).map((p) => p.kind), ['lfo']);
});
