// The speech panel: a multiline text (or CMU phonemes) editor, the mode, and the controls that belong to the chosen mode.
// Text mode shows the voice pitch; words mode shows the duration policy, the fit ceiling and the reference note. Switching mode or the
// phonemes toggle keeps both drafts, so nothing typed is lost. The source text stays the truth: every change is one setParameter.

import { el } from '../../ui.js';
import { registerStudioPanel } from './registry.js';
import { control } from './helpers.js';
import { selector } from '../selector.js';
import { quoteLiteral, unquoteLiteral } from '../../../lib/string-literal.mjs';

const MODES = [['text', 'Text (a sentence)'], ['words', 'Words (one word per note)']];
const POLICIES = [['natural', 'Natural length'], ['gate', 'Cut at the note length'], ['fit', 'Speed up to fit']];
const ENGINES = [['humanico', 'Humanico (default)'], ['retro', 'Retro']];

// the text a person typed in the field that is not in use (the renderer takes text or phonemes, never both), kept per block while the window lives
const drafts = new Map();
const plain = (raw, fallback) => (raw === undefined || raw === '' ? fallback : unquoteLiteral(raw));

export function speechPanel(context) {
  const params = context.params;
  const root = el('section', { className: 'studio-effect-panel studio-speech-panel', 'aria-label': 'Speech' });
  const state = {
    mode: plain(params.mode, 'text'),
    policy: plain(params.duration_policy, 'natural'),
    engine: plain(params.engine, 'humanico'),
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
  const showArea = () => {
    area.value = state.usePhonemes ? state.phonemes : state.text;
    area.setAttribute('aria-label', state.usePhonemes ? 'Phonemes to speak' : 'Text to speak');
    area.placeholder = state.usePhonemes ? 'HH AH0 L OW1  W ER1 L D' : 'Type what the voice says';
    hint.textContent = state.usePhonemes
      ? 'CMU phonemes with stress digits, separated by spaces. They are used instead of the text.'
      : 'Plain English text. Numbers and abbreviations are spoken as words; tick the box below for exact pronunciation.';
  };
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
  const render = () => {
    const fit = state.mode === 'words' && state.policy === 'fit';
    const items = [choice('Mode', 'mode', MODES, 'mode')];
    if (state.mode === 'words') {
      items.push(el('p', { className: 'note speech-hint', textContent: 'Connect a Notes block or a MIDI clip: each note speaks the next word, and its pitch sets the voice pitch.' }));
      items.push(choice('Duration', 'duration_policy', POLICIES, 'policy'));
      if (fit) items.push(control(context, params, 'fit_max_rate', 'Fit ceiling', 0.1, 2, '', 'linear', 70));
      items.push(control(context, params, 'reference_midi', 'Reference note', 0, 127, '', 'linear', 70));
      items.push(control(context, params, 'reference_hz', 'Reference pitch', 45, 700, 'Hz', 'log', 70));
    } else {
      items.push(control(context, params, 'pitch_hz', 'Voice pitch', 45, 700, 'Hz', 'log', 70));
    }

    phonemeRow.hidden = state.mode === 'words';
    phonemeBox.checked = state.usePhonemes;
    dynamic.replaceChildren(...items);
  };

  showArea();
  render();
  root.append(
    el('label', { className: 'studio-panel-control speech-text-row' }, [el('span', { textContent: state.usePhonemes ? 'Phonemes' : 'Text' }), area]),
    hint,
    phonemeRow,
    dynamic,
    choice('Voice', 'engine', ENGINES, 'engine'),
    control(context, params, 'rate', 'Rate', 0.1, 2, '', 'linear', 70),
    control(context, params, 'gain', 'Gain', -60, 12, 'dB', 'linear', 70),
  );
  phonemeBox.addEventListener('change', () => { root.querySelector('.speech-text-row span').textContent = state.usePhonemes ? 'Phonemes' : 'Text'; });
  return root;
}

registerStudioPanel('speech', speechPanel);
