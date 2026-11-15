import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { curveOf, fadeWeights, placeLoop, samplesOf, splitSeam } from '../loop-place.mjs';

const cases = JSON.parse(readFileSync(new URL('./fixtures/loop-place.json', import.meta.url), 'utf8'));

for (const c of cases) {
  test(`the loop placed as the server places it: ${c.name} (issue 79)`, () => {
    const seam = new Uint8Array(c.length);
    const made = placeLoop(Float32Array.from(c.stretch, (v) => v / 32768), { offset: c.offset, length: c.length, loop: c.loop, crossfade: c.crossfade, curve: c.curve, loopFrom: c.loopFrom, loopTo: c.loopTo, seam });
    assert.deepEqual([...seam], c.seam, 'the same samples are the seam');
    made.forEach((value, i) => assert.ok(Math.abs(value * 32768 - c.expected[i]) <= 1, `sample ${i}: ${value * 32768} against ${c.expected[i]}`));
  });
}

test('the fixture has seams to check: a loop with a fade, an intro and an outro all blend something', () => {
  assert.ok(cases.filter((c) => c.seam.includes(1)).length >= 8);
  assert.ok(cases.some((c) => !c.seam.includes(1)), 'and cases without one');
});

test('body plus seam is the sound, sample for sample, and each holds only its own samples', () => {
  const sound = [Float32Array.from([0.5, 0.4, 0.3, 0.2]), Float32Array.from([-0.5, -0.4, -0.3, -0.2])];
  const seam = Uint8Array.from([0, 1, 1, 0]);
  const { body, region } = splitSeam(sound, seam);
  assert.deepEqual([...body[0]].map((v) => Math.round(v * 10)), [5, 0, 0, 2]);
  assert.deepEqual([...region[1]].map((v) => Math.round(v * 10)), [0, -4, -3, 0]);
  sound.forEach((channel, c) => channel.forEach((value, i) => assert.equal(body[c][i] + region[c][i], value)));
});

test('the curves and the lengths are read like the server reads them', () => {
  assert.equal(curveOf(' "Cosine" '), 'cosine');
  assert.equal(curveOf(''), 'equal-power');
  assert.equal(curveOf('spiral'), 'equal-power');
  const w = fadeWeights('equal-power', 0.5);
  assert.ok(Math.abs(w.in ** 2 + w.out ** 2 - 1) < 1e-12);   // equal power
  assert.deepEqual(fadeWeights('linear', 0.25), { in: 0.25, out: 0.75 });
  assert.equal(samplesOf('2s', 100, 400), 200);
  assert.equal(samplesOf('2', 100, 400), 200);
  assert.equal(samplesOf('1 bar', 100, 400), 400);
  assert.equal(samplesOf('2 beats', 100, 400), 200);
  assert.equal(samplesOf('', 100, 400), null);
  assert.equal(samplesOf('3 fortnights', 100, 400), null);
});

test('the region merge of the preview adds the parts and cuts at full scale; a region into an effect is its audio input', async () => {
  const { regionLinksAsAudio, sumSaturated } = await import('../loop-place.mjs');
  const sum = sumSaturated([{ sampleRate: 10, channels: [Float32Array.from([0.5, 0.9, -0.9])] }, { sampleRate: 10, channels: [Float32Array.from([0.25, 0.4, -0.4, 0.1]), Float32Array.from([0, 0, 0, 0.2])] }]);
  assert.equal(sum.channels.length, 2);
  assert.deepEqual([...sum.channels[0]].map((v) => Math.round(v * 100) / 100), [0.75, 1, -1, 0.1]);
  assert.deepEqual([...sum.channels[1]].map((v) => Math.round(v * 100) / 100), [0.5, 0.9, -0.9, 0.2]);   // the mono part is on both sides
  const nodes = [{ id: 'loop', tpl: 'import-audio' }, { id: 'duck', tpl: 'duck' }, { id: 'merge', tpl: 'region-merge' }];
  const links = [{ from: 'loop', to: 'duck', type: 'region', inName: 'region_in' }, { from: 'duck', to: 'merge', type: 'region', inName: 'region' }];
  const read = regionLinksAsAudio(nodes, links);
  assert.deepEqual([read[0].type, read[0].inName], ['audio', 'in']);
  assert.equal(read[1], links[1], 'the link of a region merge is left as it is');
  const own = regionLinksAsAudio(nodes, [{ from: 'x', to: 'duck', type: 'audio', inName: 'in' }, links[0]]);
  assert.equal(own[1], links[0], 'an effect with an audio input of its own keeps the region link as it is');
});

test('a render link is the audio input of the effect on it, unless that effect has an audio input of its own (the chamber song)', async () => {
  const { renderLinksAsAudio } = await import('../loop-place.mjs');
  const nodes = [{ id: 'tone' }, { id: 'seat' }, { id: 'other' }];
  const render = { from: 'tone', to: 'seat', type: 'render', inName: 'render', outName: 'render' };
  const read = renderLinksAsAudio(nodes, [render]);
  assert.deepEqual([read[0].type, read[0].inName, read[0].outName, read[0].from], ['audio', 'in', 'audio', 'tone']);
  const own = { from: 'other', to: 'seat', type: 'audio', inName: 'in', outName: 'audio' };
  assert.equal(renderLinksAsAudio(nodes, [own, render])[1], render, 'an audio input of its own wins');
  const off = { ...render, disabled: true };
  assert.equal(renderLinksAsAudio(nodes, [off])[0], off);
});
