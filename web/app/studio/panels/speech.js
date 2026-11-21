// The speech panel: a multiline text (or CMU phonemes) editor, the mode, and the controls that belong to the chosen mode.
// Text mode shows the voice pitch; words mode shows the duration policy, the fit ceiling and the reference note. Switching mode or the
// phonemes toggle keeps both drafts, so nothing typed is lost. The source text stays the truth: every change is one setParameter.

import { el } from '../../ui.js';
import { registerStudioPanel } from './registry.js';
import { control } from './helpers.js';
import { selector } from '../selector.js';
import { quoteLiteral, unquoteLiteral } from '../../../lib/string-literal.mjs';
import { FLITE_PHONEMES, fliteProblem } from '../../../lib/flite-phones.mjs';
import { FLITE_PITCH_DEFAULT, LEVEL_DEFAULT, speechShown } from '../../../lib/speech-defaults.mjs';

const MODES = [['text', 'Text (a sentence)'], ['words', 'Words (one word per note)']];
const POLICIES = [['natural', 'Natural length'], ['gate', 'Cut at the note length'], ['fit', 'Speed up to fit']];
const ENGINES = [['humanico', 'Humanico (default)'], ['retro', 'Retro'], ['flite', 'Flite kal16']];
// the pitch and the pitch range that the flite engine uses when the block does not write them
const FLITE_PITCH = FLITE_PITCH_DEFAULT;
// the shaping of the flite voice (lead_trim, pause_scale, brightness) and the loudness choice for every engine
const LEAD_TRIMS = [['off', 'Keep the pause of the voice'], ['on', 'Start on the beat']];
const LEVELS = [['matched', 'Equal across engines (default)'], ['engine', 'Each engine as it is']];

// the text a person typed in the field that is not in use (the renderer takes text or phonemes, never both), kept per block while the window lives
const drafts = new Map();
// the blocks whose table of valid phonemes is open: the panel is built again after every change of the source, and an open table must stay open
const openTables = new Set();
// where the cursor was in the field of a block when it was left: the panel is built again after every change of the source, and a tap on a phoneme
// (which leaves the field) must still insert where the person had put the cursor
const carets = new Map();
const plain = (raw, fallback) => (raw === undefined || raw === '' ? fallback : unquoteLiteral(raw));

export function speechPanel(context) {
  const params = context.params;
  const root = el('section', { className: 'studio-effect-panel studio-speech-panel', 'aria-label': 'Speech' });
  const state = {
    mode: plain(params.mode, 'text'),
    policy: plain(params.duration_policy, 'natural'),
    engine: plain(params.engine, 'humanico'),
    leadTrim: plain(params.lead_trim, 'off'),
    level: plain(params.level, LEVEL_DEFAULT),
    text: plain(params.text, ''),
    phonemes: plain(params.phonemes, ''),
  };
  const kept = drafts.get(context.block) || {};
  if (state.text === '' && kept.text) state.text = kept.text;
  if (state.phonemes === '' && kept.phonemes) state.phonemes = kept.phonemes;
  // a block that already has phonemes opens in phonemes mode (phonemes exist in text mode only)
  state.usePhonemes = plain(params.phonemes, '') !== '' && state.mode === 'text';
  const keep = () => drafts.set(context.block, { text: state.text, phonemes: state.phonemes });
  const commit = (items) => (context.commitMany ? context.commitMany(items) : items.forEach((item) => context.commit(item.name, item.value)));   // { name, remove: true } removes the line
  const choice = (label, name, items, key) => el('label', { className: 'studio-panel-control' }, [el('span', { textContent: label }), selector({
    label, value: state[key], disabled: context.readOnly, choices: items.map(([value, text]) => ({ value, label: text })),
    onCommit: (value) => {
      state[key] = value;
      const items = [{ name, value: quoteLiteral(value) }];
      const written = Number.parseFloat(String(params.pitch_hz ?? ''));
      // the pitch that goes with the voice: flite sits lower than the other engines
      if (name === 'engine' && value === 'flite' && (!Number.isFinite(written) || written === 105)) items.push({ name: 'pitch_hz', value: FLITE_PITCH });
      if (name === 'engine' && value !== 'flite' && written === 95) items.push({ name: 'pitch_hz', value: '105 Hz' });
      if (name === 'engine' && value !== 'flite') {
        // voice and pitch_range belong to flite: the renderer refuses them next to another engine
        if (params.voice !== undefined) items.push({ name: 'voice', remove: true });
        if (params.pitch_range !== undefined) items.push({ name: 'pitch_range', remove: true });
        for (const name of ['lead_trim', 'pause_scale', 'brightness']) if (params[name] !== undefined) items.push({ name, remove: true });
      }
      if (name === 'mode' && value === 'words' && state.usePhonemes) {
        // phonemes exist in text mode only: the line goes, and the text draft (if any) comes back
        keep();
        state.usePhonemes = false;
        items.push({ name: 'phonemes', remove: true });
        if (state.text !== '') items.push({ name: 'text', value: quoteLiteral(state.text) });
      }
      commit(items); showArea(); render();
    },
  })]);

  const area = el('textarea', { className: 'speech-text', rows: 5, disabled: context.readOnly, spellcheck: false });
  const hint = el('p', { className: 'note speech-hint' });
  const phonemeBox = el('input', { type: 'checkbox', checked: state.usePhonemes, disabled: context.readOnly });
  // the flite voice names a phoneme it does not know before the build does (the build still refuses it with AMP5351); the other engines have their own table
  const phoneNote = el('div', { className: 'note speech-phone-error', role: 'status', hidden: true });
  // the palette of the flite phonemes: one closed line ("Valid phonemes (40)") that a tap opens; a tap on a phoneme puts it in the field at the cursor
  const palette = el('details', { className: 'speech-phone-list', hidden: true });
  palette.append(el('summary', { textContent: `Valid phonemes (${FLITE_PHONEMES.length})` }));
  const keys = el('div', { className: 'speech-phone-keys' });
  palette.append(keys);
  palette.open = openTables.has(context.block);
  palette.addEventListener('toggle', () => { if (palette.open) openTables.add(context.block); else openTables.delete(context.block); });
  const insertPhone = (phone) => {
    const text = area.value;
    const [from, to] = document.activeElement === area ? [area.selectionStart, area.selectionEnd] : (carets.get(context.block) ?? [text.length, text.length]);
    const start = Math.min(from ?? text.length, text.length), end = Math.min(Math.max(to ?? start, start), text.length);
    const before = text.slice(0, start), after = text.slice(end);
    // phonemes are separated by spaces: one is added on the side where the neighbour is not a space already
    const left = before === '' || /\s$/.test(before) ? '' : ' ';
    const right = after !== '' && !/^\s/.test(after) ? ' ' : '';
    const put = before + left + phone + right;
    area.value = put + after;
    area.setSelectionRange(put.length, put.length);   // no focus(): on a touch screen that would raise the keyboard
    carets.set(context.block, [put.length, put.length]);   // the next tap goes behind this phoneme, also in the panel that is built again
    state.phonemes = area.value;
    keep();
    writeUsed(true);
    checkPhones();
  };
  for (const phone of FLITE_PHONEMES) {
    keys.append(el('button', {
      type: 'button', className: 'speech-phone-key', textContent: phone, title: `Insert ${phone}`, 'aria-label': `Insert phoneme ${phone}`,
      disabled: context.readOnly, onclick: () => insertPhone(phone),
    }));
  }
  const checkPhones = () => {
    const flitePhones = state.engine === 'flite' && state.usePhonemes;
    const found = flitePhones ? fliteProblem(area.value) : null;
    area.setAttribute('aria-invalid', found ? 'true' : 'false');
    phoneNote.hidden = !found;
    phoneNote.replaceChildren();
    palette.hidden = !flitePhones;
    // the sentence only: the palette below it is where the valid phonemes are
    if (found) phoneNote.append(el('p', { textContent: found.message.replace(/ The phonemes are:.*$/, '') }));
  };
  const showArea = () => {
    area.value = state.usePhonemes ? state.phonemes : state.text;
    area.setAttribute('aria-label', state.usePhonemes ? 'Phonemes to speak' : 'Text to speak');
    area.placeholder = state.usePhonemes ? 'HH AH0 L OW1  W ER1 L D' : 'Type what the voice says';
    hint.textContent = state.usePhonemes
      ? 'CMU phonemes with stress digits, separated by spaces. They are used instead of the text.'
      : 'Plain English text. Numbers and abbreviations are spoken as words; tick the box below for exact pronunciation.';
    if (state.usePhonemes && state.engine === 'flite') {
      hint.textContent += ' Flite speaks them as one syllable without phrasing or pauses, so they sound flatter than text; a stress digit only picks the reduced vowels (AH0, ER0).';
    }
    checkPhones();
  };
  area.addEventListener('input', checkPhones);
  area.addEventListener('blur', () => carets.set(context.block, [area.selectionStart, area.selectionEnd]));
  // the draft is written when the field is left (change), never on every key, so one edit is one undo step
  // the used field is written and the other one is removed from the source in one step: the renderer refuses both, and refuses an empty
  // line. An empty field removes its own line; switching to a field that is still empty changes nothing until something is typed.
  const writeUsed = (fromField) => {
    const [used, other] = state.usePhonemes ? ['phonemes', 'text'] : ['text', 'phonemes'];
    if (state[used] === '') { if (fromField) commit([{ name: used, remove: true }]); return; }
    commit([{ name: used, value: quoteLiteral(state[used]) }, { name: other, remove: true }]);
  };
  area.onchange = () => {
    if (state.usePhonemes) state.phonemes = area.value; else state.text = area.value;
    keep();
    writeUsed(true);
  };
  phonemeBox.onchange = () => {
    if (state.usePhonemes) state.phonemes = area.value; else state.text = area.value;   // keep what was typed in the field that is leaving
    state.usePhonemes = phonemeBox.checked;
    keep();
    showArea();
    writeUsed();
  };

  const phonemeRow = el('label', { className: 'speech-phoneme-toggle' }, [phonemeBox, ' Write phonemes instead of text']);
  const dynamic = el('div', { className: 'speech-mode-controls' });
  const modeSlot = el('div', { className: 'speech-mode-slot' });   // the Mode choice: it is made again after every change, next to the Voice choice
  const render = () => {
    const fit = state.mode === 'words' && state.policy === 'fit';
    const flite = state.engine === 'flite';
    const shown = speechShown(params, state.engine);   // an unwritten parameter shows what the block does, not the bottom of its knob
    modeSlot.replaceChildren(choice('Mode', 'mode', MODES, 'mode'));
    const items = [];
    if (state.mode === 'words') {
      items.push(el('p', { className: 'note speech-hint', textContent: 'Connect a Notes block or a MIDI clip: each note speaks the next word, and its pitch sets the voice pitch.' }));
      items.push(choice('Duration', 'duration_policy', POLICIES, 'policy'));
      if (fit) items.push(control(context, shown, 'fit_max_rate', 'Fit ceiling', 0.1, 2, '', 'linear', 70));
      items.push(control(context, shown, 'reference_midi', 'Reference note', 0, 127, '', 'linear', 70));
      items.push(control(context, shown, 'reference_hz', 'Reference pitch', 45, 700, 'Hz', 'log', 70));
      // the voice sings one steady pitch of 50 to 480 Hz per word; a note beyond that is sung an octave or more away (the take says so)
      if (flite) {
        items.push(el('p', {
          className: 'note speech-hint speech-flite-range',
          textContent: 'Flite sings a pitch from 50 to 480 Hz: a note outside that range is sung an octave or more away. Every word is sung at one steady pitch.',
        }));
      }
    } else {
      // the pitch and the pitch range are two knobs of one row (a narrow panel stacks them)
      const knobs = [control(context, shown, 'pitch_hz', 'Voice pitch', 45, 700, 'Hz', 'log', 70)];
      knobs.push(flite ? control(context, shown, 'pitch_range', 'Pitch range', 0, 200, 'Hz', 'linear', 70) : control(context, shown, 'rate', 'Rate', 0.1, 2, '', 'linear', 70));
      items.push(el('div', { className: 'speech-knob-row' }, knobs));
      if (flite) items.push(choice('Leading silence', 'lead_trim', LEAD_TRIMS, 'leadTrim'));
      if (flite) {
        // the shaping of the voice: how long its pauses are and how bright it is, two knobs of one row
        const shaping = [control(context, shown, 'pause_scale', 'Pause length', 0, 4, '', 'linear', 70), control(context, shown, 'brightness', 'Brightness', 0, 12, 'dB', 'linear', 70)];
        items.push(el('div', { className: 'speech-knob-row' }, shaping));
      }
      if (flite) items.push(el('p', { className: 'note speech-hint', textContent: 'Flite (kal16) is a classic diphone voice for plain English text.' }));
    }
    // the rate and the gain: the rate is already next to the pitch in the text mode of the engines that have no pitch range
    const rateAbove = state.mode === 'text' && !flite;
    const rateKnob = rateAbove ? [] : [control(context, shown, 'rate', 'Rate', 0.1, 2, '', 'linear', 70)];
    items.push(el('div', { className: 'speech-knob-row' }, [...rateKnob, control(context, shown, 'gain', 'Gain', -60, 12, 'dB', 'linear', 70)]));

    phonemeRow.hidden = state.mode === 'words';
    phonemeBox.checked = state.usePhonemes;
    dynamic.replaceChildren(...items);
  };

  showArea();
  render();
  root.append(
    el('label', { className: 'studio-panel-control speech-text-row' }, [el('span', { textContent: state.usePhonemes ? 'Phonemes' : 'Text' }), area]),
    hint,
    phoneNote,
    palette,
    phonemeRow,
    el('div', { className: 'speech-select-row' }, [choice('Voice', 'engine', ENGINES, 'engine'), modeSlot]),
    dynamic,
    choice('Loudness', 'level', LEVELS, 'level'),
  );
  phonemeBox.addEventListener('change', () => { root.querySelector('.speech-text-row span').textContent = state.usePhonemes ? 'Phonemes' : 'Text'; });
  return root;
}

registerStudioPanel('speech', speechPanel);
