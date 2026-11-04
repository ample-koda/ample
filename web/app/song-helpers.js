// The helpers of the song text as a table (cached by the text): the editor completes them, shows their signatures and the roll evaluates pipelines that use them. @ample:song-helpers
import { helperTableFrom } from '../lib/note-pipeline.mjs';
import { helperSourcesOf } from '../lib/note-lang.mjs';

let cache = { text: null, table: null };

/** The helper table of a song text (built again only when the text changed). */
export function helpersOfText(text) {
  const source = String(text ?? '');
  if (cache.text !== source) cache = { text: source, table: helperTableFrom(helperSourcesOf(source)) };
  return cache.table;
}

/** The completion items for the helpers of a song: after a pipe, helpers that take Notes first (without that first argument); at the start of an expression, helpers that make Notes. */
const VOLATILE = [['currentTime', 'clock', 'the time of day'], ['currentDate', 'clock', 'the date'], ['osRandom', 'os-random', 'a number from the operating system'], ['environment', 'environment', 'a variable of the machine']];

export function helperItems(text, { afterPipe, capabilities = [] }) {
  const signatures = helpersOfText(text).signatures();
  const volatile = afterPipe ? [] : VOLATILE.map(([name, capability, what]) => {
    const state = capabilities.find((c) => c.name === capability);
    const off = state && !state.effective;
    return { label: name, insertText: name + (name === 'environment' ? '("")' : '()'), detail: `volatile: ${what}${off ? ' (off: ' + state.reason + ')' : ''}`, disabled: !!off };
  });
  return [...volatile, ...Object.values(signatures)
    .filter((sig) => (afterPipe ? sig.receiver === 'Notes' : sig.returns === 'Notes'))
    .map((sig) => {
      const rest = afterPipe ? sig.params.slice(1) : sig.params;
      const required = rest.filter((p) => p.required);
      return { label: sig.id, insertText: sig.id + '(' + required.map((p) => p.name + '=').join(', ') + ')', detail: sig.doc + ' (' + sig.params.map((p) => p.type + ' ' + p.name).join(', ') + ') -> ' + sig.returns };
    })];
}
