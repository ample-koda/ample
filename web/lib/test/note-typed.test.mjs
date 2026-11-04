import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { evaluate } from '../note-pipeline.mjs';
import { activeParam, argumentNote, callContext, noteNameToPitch, signatureOf, typedCompletion } from '../note-typed.mjs';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const notes = (text) => { const r = evaluate(text); assert.deepEqual(r.errors, []); return r.notes; };
const labels = (before) => (typedCompletion(before)?.items ?? []).map((i) => i.label);

test('the generated catalogue is the one of spec/note-ops.json', () => {
  execFileSync('node', [path.join(root, 'tools', 'gen-note-catalog.mjs'), '--check'], { stdio: 'pipe' });
});

test('typed spellings mean the same as the plain ones (the twin of the C# test)', () => {
  const source = '1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100';
  assert.equal(notes(`${source} |> transpose(+7st)`), notes(`${source} |> transpose(+7)`));
  assert.equal(notes(`${source} |> transpose(-1oct)`), notes(`${source} |> transpose(-12)`));
  assert.equal(notes(`${source} |> transpose(200cents)`), notes(`${source} |> transpose(+2)`));
  assert.equal(notes(`${source} |> thin(25%, seed=1)`), notes(`${source} |> thin(0.25, seed=1)`));
  assert.equal(notes('euclid(3, 8, pitch=C2)'), notes('euclid(3, 8, pitch=36)'));
  assert.equal(notes('euclid(3, 8, pitch=Eb4)'), notes('euclid(3, 8, pitch=63)'));
});

test('note names follow the MIDI convention (C4 is 60)', () => {
  assert.equal(noteNameToPitch('C4'), 60);
  assert.equal(noteNameToPitch('A0'), 21);
  assert.equal(noteNameToPitch('Bb2'), 46);
  assert.equal(noteNameToPitch('H2'), null);
  assert.equal(noteNameToPitch('C10'), null);
});

test('the call around the cursor: name, argument index, named argument and string', () => {
  assert.deepEqual({ ...callContext('notes walk('), used: [...callContext('notes walk(').used] }, { call: 'walk', piped: false, index: 0, named: null, valueText: '', inString: false, used: [] });
  const c = callContext('notes walk(C, minor, step=0.5');
  assert.equal(c.index, 2);
  assert.equal(c.named, 'step');
  assert.equal(c.valueText, '0.5');
  assert.equal(callContext('notes a.notes |> shift(1beat) |> '), null);
  assert.equal(callContext('chords("Am F').inString, true);
  assert.equal(callContext('x |> humanize(timing=20ms, seed=stable("a").fork(').call, 'fork');
  assert.equal(callContext('notes walk(C) # shift('), null);
});

test('walk( offers pitch classes, then only scales, then durations for step=', () => {
  const first = labels('notes walk(');
  assert.ok(first.includes('C') && first.includes('F#') && first.includes('steps='));
  assert.ok(!first.includes('minor'));
  const second = labels('notes walk(C, ');
  assert.ok(second.includes('minor') && second.includes('dorian'));
  assert.ok(!second.includes('C') && !second.includes('F#'));
  assert.deepEqual(labels('notes walk(C, min'), ['minor']);
  const step = labels('notes walk(C, minor, step=');
  assert.ok(step.includes('0.5beat') && step.includes('250ms') && !step.includes('minor'));
  assert.ok(labels('notes walk(C, minor, octave=').includes('4'));
  assert.equal(typedCompletion('notes walk(C, minor, seed='), null);   // seeds have their own completion
});

test('other places are typed too: intervals, probabilities, chords in a string, choices, windows', () => {
  assert.ok(labels('a.notes |> transpose(').includes('+7st'));
  assert.ok(labels('a.notes |> thin(').includes('25%'));
  assert.deepEqual(labels('a.notes |> thin(0.5, seed=1, per='), ['note', 'bar']);
  assert.ok(labels('chords("Am F ').includes('G7'));
  assert.ok(labels('a.notes |> bars(').includes('1..5'));
  assert.ok(labels('a.notes |> humanize(timing=').includes('20ms') === false && labels('a.notes |> humanize(timing=').includes('0.25beat'));
  assert.equal(typedCompletion('a.notes |> unknown('), null);
});

test('signature help shows the real types, defaults and the active parameter', () => {
  const sig = signatureOf('walk');
  assert.equal(sig.params[0].text, 'PitchClass root');
  assert.equal(sig.params[2].text, 'Count steps = 16');
  assert.equal(sig.returns, 'Notes');
  assert.equal(activeParam(callContext('walk(C, ')).name, 'scale');
  assert.equal(activeParam(callContext('walk(C, minor, octave=')).name, 'octave');
  assert.equal(signatureOf('where').params[0].type, 'Range<Pitch>');
  assert.equal(signatureOf('nothing'), null);
});

test('argument notes explain what is converted', () => {
  assert.match(argumentNote(callContext('a.notes |> transpose(+7')), /semitones/);
  assert.match(argumentNote(callContext('a.notes |> thin(25%')), /0\.25/);
  assert.match(argumentNote(callContext('euclid(3, 8, pitch=C2')), /MIDI 36/);
  assert.match(argumentNote(callContext('a.notes |> shift(2')), /needs a unit/);
  assert.equal(argumentNote(callContext('a.notes |> shift(2beat')), '');
});

test('the helpers of a song are completed and shown with their real types', async () => {
  const { helperTableFrom } = await import('../note-pipeline.mjs');
  const table = helperTableFrom([{ text: 'helper canonVoice(Notes source, Interval by, Duration delay = 2bars) -> Notes {\n  source |> transpose(by) |> shift(delay)\n}', line: 0 }, { text: 'dynamic helper variation() -> Int { currentTime().minute }', line: 5 }]);
  const extra = table.signatures();
  assert.equal(extra['canonVoice'].receiver, 'Notes');
  assert.equal(extra['canonVoice'].params[2].default, '2bars');
  assert.equal(extra.variation.effect, 'dynamic');
  const sig = signatureOf('canonVoice', extra);
  assert.equal(sig.params[1].text, 'Interval by');
  assert.equal(sig.params[2].text, 'Duration delay = 2bars');
  const items = (typedCompletion('a.notes |> canonVoice(', extra)?.items ?? []).map((i) => i.label);
  assert.ok(items.includes('by=') && items.includes('delay='));
  assert.ok((typedCompletion('a.notes |> canonVoice(+7st, ', extra)?.items ?? []).some((i) => i.label === '2bars'));
  assert.equal(activeParam(callContext('canonVoice(theme.notes, +7st, delay='), extra).name, 'delay');
  assert.equal(activeParam(callContext('a.notes |> canonVoice(+7st, '), extra).name, 'delay');   // the piped notes are the first argument
  assert.equal(typedCompletion('a.notes |> unknown-helper(', extra), null);
});
