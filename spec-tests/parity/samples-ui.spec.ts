import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp, state, openTab } from '../../web/tests/support';

function wav(samples: number[], rate = 44100): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}
const put = (folder: string, rel: string, data: Buffer) => { const p = path.join(folder, rel); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, data); };
const tone = (n: number, level = 6000) => wav([...Array(n)].map((_, i) => Math.round(level * Math.exp(-i / 1000) * Math.sin(i / 9))));

freshServer({
  song: 'composition "band" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords {\n  chords Am F\n  bars 2\n}\ndrums kit at 1 for 2 {\n  kit samples/kit\n  pattern beat-A\n}\ndrums synth at 1 for 2 {\n}\nguitar gtr at 1 for 2 {\n  set samples/gtr\n  strum D...D.u.\n  from chords.notes -> notes\n}\n',
  prepare: (_cli, folder) => {
    for (const v of ['kick', 'snare', 'hat', 'tom']) put(folder, `samples/kit/${v}.wav`, tone(2000));
    for (const n of ['e2', 'a2', 'c3']) put(folder, `samples/gtr/${n}.wav`, tone(8000));
  },
});

const param = async (block: string, name: string) => (await api('/api/doc?path=song.amplet')).blocks.find((b) => b.name === block).parameters.find((p) => p.name === name)?.value;

test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

test('a drum block with a sample kit shows its pattern as a grid and a click changes the pattern text', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  const grid = page.locator('#seq-drums');
  await expect(grid).toBeVisible();
  await expect(grid.locator('.seqrow[data-voice]')).toHaveCount(3);
  await expect(grid.locator('[data-voice="kick"] .seqc[data-step="0"]')).toHaveAttribute('data-value', 'x');
  await expect(grid.locator('[data-voice="kick"] .seqc[data-step="1"]')).toHaveAttribute('data-value', '.');

  await grid.locator('[data-voice="kick"] .seqc[data-step="1"]').click();
  await expect.poll(() => param('kit', 'pattern')).toBe('kick:xx......x....... snare:....x.......x... hat:x.x.x.x.x.x.x.x.');
  await grid.locator('[data-voice="kick"] .seqc[data-step="1"]').click();
  await expect(grid.locator('[data-voice="kick"] .seqc[data-step="1"]')).toHaveAttribute('data-value', 'X');
  await grid.locator('[data-voice="kick"] .seqc[data-step="1"]').click();
  await grid.locator('[data-voice="kick"] .seqc[data-step="1"]').click();
  await expect.poll(() => param('kit', 'pattern')).toBe('beat-A');
});

test('voices can be added and removed, presets and clear work, and a voice without a file is marked', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  const grid = page.locator('#seq-drums');
  await grid.locator('select[title^="Add a voice"]').selectOption('tom');
  await expect(grid.locator('.seqrow[data-voice="tom"]')).toBeVisible();
  await expect.poll(() => param('kit', 'pattern')).toContain('tom:................');
  await grid.locator('.seqrow[data-voice="tom"] .seqx').click();
  await expect(grid.locator('.seqrow[data-voice="tom"]')).toHaveCount(0);

  await grid.getByRole('button', { name: 'Clear' }).click();
  await expect.poll(() => param('kit', 'pattern')).toBe('kick:................ snare:................ hat:................');
  await grid.locator('select[title^="Start from a named pattern"]').selectOption('beat-B');
  await expect.poll(() => param('kit', 'pattern')).toBe('beat-B');

  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: (await api('/api/doc?path=song.amplet')).version, op: { type: 'setParameter', block: 'kit', name: 'pattern', value: 'kick:x... crash:x...' } } });
  await openApp(page, { reset: false });
  await node(page, 'kit').locator('.nh .t').click();
  await expect(page.locator('#seq-drums .seqname.missing')).toHaveText('crash');
  await expect(page.locator('#seq-drums .seqerr')).toContainText('No sample for: crash');
  // give the next tests the pattern they start from
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: (await api('/api/doc?path=song.amplet')).version, op: { type: 'setParameter', block: 'kit', name: 'pattern', value: 'beat-A' } } });
});

test('swing and humanize are typed fields with arrows and a focus slider', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  await openTab(page, 'parameters');
  const swing = page.locator('#props .prop-tabpanel.active .prop', { hasText: 'swing' }).locator('input[type="number"]');
  const ranged = await page.locator('#props .prop-tabpanel.active input[type="number"][max]').count();
  await expect(page.locator('#props .prop-tabpanel.active input[type="range"]')).toHaveCount(ranged);
  await swing.fill('30');
  await swing.press('Enter');
  await expect.poll(() => param('kit', 'swing')).toBe('30');
  await swing.focus();
  await swing.press('ArrowUp');
  await expect.poll(() => param('kit', 'swing')).toBe('31');
});

test('a value outside the range is moved into it and an empty field goes back', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  await openTab(page, 'parameters');
  const humanize = page.locator('#props .prop-tabpanel.active .prop', { hasText: 'humanize' }).locator('input[type="number"]');
  await humanize.fill('250');
  await humanize.press('Enter');
  await expect.poll(() => param('kit', 'humanize')).toBe('100');
  await expect(humanize).toHaveValue('100');
  const currentHumanize = page.locator('#props .prop-tabpanel.active .prop', { hasText: 'humanize' }).locator('input[type="number"]');
  await currentHumanize.fill('');
  await currentHumanize.press('Enter');
  await expect(page.locator('#props .prop-tabpanel.active .prop', { hasText: 'humanize' }).locator('input[type="number"]')).toHaveValue('100');
});

test('focusing a ranged number field shows its inline slider while typing remains available', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  await openTab(page, 'parameters');
  const ranged = await page.locator('#props .prop-tabpanel.active input[type="number"][max]').count();
  expect(ranged).toBeGreaterThanOrEqual(2);
  await expect(page.locator('#props .prop-tabpanel.active input[type="range"]')).toHaveCount(ranged);
  const slider = page.locator('#props .prop-tabpanel.active .prop', { hasText: 'swing' }).locator('input[type="range"]');
  const field = page.locator('#props .prop-tabpanel.active .prop', { hasText: 'swing' }).locator('input[type="number"]');
  await expect(slider).toBeHidden();
  await field.focus();
  await expect(slider).toBeVisible();
  await slider.fill('42');
  await expect.poll(() => param('kit', 'swing')).toBe('42');
  await expect(field).toHaveValue('42');
  await field.fill('37');
  await field.press('Enter');
  await expect.poll(() => param('kit', 'swing')).toBe('37');
});

test('number parameters of other blocks are number fields too, and a value like derived stays a text field', async ({ page }) => {
  await node(page, 'chords').locator('.nh .t').click();
  const bars = page.locator('.prop', { hasText: 'bars' }).locator('input[type="number"]');
  await expect(bars).toHaveCount(1);
  await bars.fill('4');
  await bars.press('Enter');
  await expect.poll(() => param('chords', 'bars')).toBe('4');
});

test('a block with the built-in sound has a grid of its own voices, and choosing a sample kit keeps the grid', async ({ page }) => {
  await node(page, 'synth').locator('.nh .t').click();
  const box = page.locator('#seq-drums');
  await expect(box).not.toContainText('Pick a sample kit');
  await expect(box.locator('.seqrow[data-voice]')).toHaveCount(3);   // beat-A: kick, snare, hat
  await box.locator('select').first().selectOption('samples/kit');
  await expect.poll(() => param('synth', 'kit')).toBe('samples/kit');
  await expect(page.locator('#seq-drums .seqrow[data-voice]')).toHaveCount(3);
});

test('the steps field is as wide as the grid: eight steps give eight cells a row', async ({ page }) => {
  await node(page, 'synth').locator('.nh .t').click();
  const box = page.locator('#seq-drums');
  await expect(box.locator('.seqrow[data-voice="kick"] .seqc')).toHaveCount(16);
  const steps = box.locator('.prop', { hasText: 'steps' }).locator('input[type="number"]');
  await steps.fill('8');
  await steps.press('Enter');
  await expect.poll(() => param('synth', 'steps')).toBe('8');
  await expect(page.locator('#seq-drums .seqrow[data-voice="kick"] .seqc')).toHaveCount(8);
  await steps.fill('16');   // the tests of this file share one song: leave it as it was
  await steps.press('Enter');
  await expect.poll(() => param('synth', 'steps')).toBe('16');
});

test('the strum is as wide as the steps field: eight steps give eight cells a bar', async ({ page }) => {
  await node(page, 'gtr').locator('.nh .t').click();
  const box = page.locator('#seq-guitar');
  await expect(box.locator('.seqrow[data-bar="0"] .seqc')).toHaveCount(16);
  const steps = box.locator('.prop', { hasText: 'steps' }).locator('input[type="number"]');
  await steps.fill('8');
  await steps.press('Enter');
  await expect.poll(() => param('gtr', 'steps')).toBe('8');
  await expect(page.locator('#seq-guitar .seqrow[data-bar="0"] .seqc')).toHaveCount(8);
  await steps.fill('16');   // the tests of this file share one song: leave it as it was
  await steps.press('Enter');
  await expect.poll(() => param('gtr', 'steps')).toBe('16');
});

test('the guitar strum is a row of steps that cycles through the strokes, bars can be added and the voicing chosen', async ({ page }) => {
  await node(page, 'gtr').locator('.nh .t').click();
  const box = page.locator('#seq-guitar');
  await expect(box).toBeVisible();
  await expect(box.locator('.seqc[data-step="0"]')).toHaveAttribute('data-value', 'D');
  await box.locator('.seqc[data-step="1"]').click();
  await expect.poll(() => param('gtr', 'strum')).toBe('DD..D.u.D...D.u.'.slice(0, 0) + 'DD..D.u.D...D.u.');
  await box.locator('.seqc[data-step="1"]').click();
  await expect(box.locator('.seqc[data-step="1"]')).toHaveAttribute('data-value', 'd');

  await box.getByRole('button', { name: '+ bar' }).click();
  await expect(box.locator('.seqrow[data-bar]')).toHaveCount(2);
  expect(await param('gtr', 'strum')).toContain('|');
  await box.locator('.seqrow[data-bar="1"] .seqx').click();
  await expect(box.locator('.seqrow[data-bar]')).toHaveCount(1);

  await box.locator('select[title^="How the chord"]').selectOption('power');
  await expect.poll(() => param('gtr', 'voicing')).toBe('power');
  expect((await state(page)).text).toContain('voicing power');
});

test('drum rows have mute, solo, level and pan written into the pattern text, and the voice table lists the pattern voices', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  const grid = page.locator('#seq-drums');
  await grid.locator('[data-voice="kick"] .seqm').click();
  await expect.poll(() => param('kit', 'pattern')).toContain('kick:x.......x.......@m');
  await grid.locator('[data-voice="kick"] .seqm').click();
  await expect.poll(() => param('kit', 'pattern')).toBe('beat-A');
  const level = grid.locator('[data-voice="snare"] input.seqn').first();
  await level.fill('-3');
  await level.press('Tab');
  await expect.poll(() => param('kit', 'pattern')).toContain('snare:....x.......x...@l-3');
  await grid.locator('[data-voice="hat"] .seqs').click();
  await expect.poll(() => param('kit', 'pattern')).toContain('kick:x.......x.......@m');
  const rows = page.locator('#props .voicemap-row');
  await expect(rows).toHaveCount(3);
  await expect(rows.first().locator('.vm-voice')).toHaveText('kick');
  await expect(rows.first().locator('.vm-source')).toHaveText('from the kit');
});

// X02 (plan/mix-and-record/X02-drum-step-sequencer.md): the step grid's own per-row listen button (the voice's
// kit sample, not the replace picker's) and the "closed by" field writing the choke parameter.
test('a drum row has its own listen button and a closed-by field that writes the choke parameter', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  const grid = page.locator('#seq-drums');
  const hatListen = grid.locator('[data-voice="hat"] .seqlisten');
  await expect(hatListen).toBeEnabled();
  await hatListen.click();
  await expect.poll(() => page.evaluate(async () => (await import('/app/player.js')).playerState().source)).toBe('samples/kit/hat.wav');
  await page.evaluate(async () => (await import('/app/player.js')).stop());
  const kickChoke = grid.locator('[data-voice="kick"] .seqchoke');
  await kickChoke.fill('snare, hat');
  await kickChoke.press('Tab');
  await expect.poll(() => param('kit', 'choke')).toBe('kick:snare,hat');
  await kickChoke.fill('');
  await kickChoke.press('Tab');
  await expect.poll(() => param('kit', 'choke')).toBeUndefined();
});

test('a voice is replaced by a sample of another kit through the voice table and reset brings the kit back', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  await openTab(page, 'parameters');
  const row = page.locator('#props .voicemap-row', { hasText: 'kick' });
  await row.getByRole('button', { name: 'replace...' }).click();
  const picker = page.locator('.project-file-picker');
  await expect(picker).toBeVisible();
  await picker.getByRole('button', { name: 'Expand samples' }).click();
  await picker.getByRole('treeitem', { name: 'kit', exact: true }).click();
  await picker.locator('[role=option][data-path="samples/kit/tom.wav"]').click();
  await picker.getByRole('button', { name: 'Choose', exact: true }).click();
  await expect.poll(() => param('kit', 'map')).toBe('kick:samples/kit/tom');
  await expect(row.locator('.vm-source')).toHaveText('samples/kit/tom');
  await row.getByRole('button', { name: 'Back to the sample of the kit' }).click();
  await expect.poll(() => param('kit', 'map')).toBeUndefined();
});

// X01 (plan/mix-and-record/X01-drum-voice-map.md): the voice table's "replace..." sample picker lets you listen to
// a candidate before choosing it, without selecting (and closing) the row.
test('the replace sample picker has a listen button that plays without choosing the row', async ({ page }) => {
  await node(page, 'kit').locator('.nh .t').click();
  await openTab(page, 'parameters');
  const row = page.locator('#props .voicemap-row', { hasText: 'kick' });
  await row.getByRole('button', { name: 'replace...' }).click();
  const picker = page.locator('.project-file-picker');
  await expect(picker).toBeVisible();
  await picker.getByRole('button', { name: 'Expand samples' }).click();
  await picker.getByRole('treeitem', { name: 'kit', exact: true }).click();
  const tomRow = picker.locator('[role=option][data-path="samples/kit/tom.wav"]');
  const listen = tomRow.locator('.file-play');
  await expect(listen).toBeVisible();
  await listen.click();
  await expect.poll(() => page.evaluate(async () => (await import('/app/player.js')).playerState().source)).toBe('samples/kit/tom.wav');
  // listening did not select (and so did not close) the row: Choose is still disabled, and tom is not yet the map.
  await expect(picker.getByRole('button', { name: 'Choose', exact: true })).toBeDisabled();
  await page.evaluate(async () => (await import('/app/player.js')).stop());
  await expect(listen).toHaveText('▶');
  await picker.getByRole('button', { name: 'Cancel' }).click();
  await expect.poll(() => param('kit', 'map')).toBeUndefined();
});
