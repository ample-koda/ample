// The log view of a song, as the Studio song log and Remix show it (plan/diagnostics WP8): the events of log, debug, trace and warn in the order they happened,
// filtered by level, with repeated events folded into one row with a count, and the budget notices of the sink (AMP6007) shown as what was left out.
// Pure functions over the events the server sends ({seq, level, code, message, hint, context, origin, line, col, stack}); no page, so they are tested in Node.

/** The levels of the log view, most important first. */
export const LEVELS = ['warning', 'info', 'debug', 'trace'];

/** The code of the notice the sink adds when a budget left events out. */
export const BUDGET_CODE = 'AMP6007';

const contextText = (context) => (context == null ? '' : typeof context === 'string' ? context : JSON.stringify(context));

/** Where an event happened: line:col, and the component when it is not the song itself. */
export function whereText(event) {
  const at = event.line > 0 ? `${event.line}:${event.col > 0 ? event.col : 1}` : '';
  const origin = event.origin && event.origin !== 'song' ? event.origin.replace(/^component:/, '') : '';
  return [at, origin].filter(Boolean).join(' in ');
}

/** The text of an event: the message, its code and its context (the message and the hint are already redacted and cut by the server). */
export function messageText(event) {
  return `${event.message ?? ''}${event.code ? ` [${event.code}]` : ''}${event.context != null ? ` ${contextText(event.context)}` : ''}`;
}

/** Whether an event can lead to a line of the song (an event of a component is in another file). */
export const inSong = (event) => event.line > 0 && (!event.origin || event.origin === 'song');

/**
 * The rows of the log view: the events of the levels that are shown, in the order they happened; events that are the same (level, code, message, context and place)
 * are one row with a count and the place of the first. Budget notices are rows of their own (kind "budget"), whatever the levels shown.
 * @param {Array<object>} events the events of the song
 * @param {Iterable<string>} [shown] the levels to show (all when left out)
 * @returns {Array<{kind: 'event'|'budget', event: object, count: number}>}
 */
export function logRows(events, shown = LEVELS) {
  const levels = new Set(shown);
  const rows = [];
  const byKey = new Map();
  for (const event of [...(events ?? [])].sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0))) {
    if (event.code === BUDGET_CODE) { rows.push({ kind: 'budget', event, count: 1 }); continue; }
    if (!levels.has(event.level)) continue;
    const key = [event.level, event.code ?? '', event.message ?? '', contextText(event.context), event.origin ?? '', event.line ?? 0, event.col ?? 0].join('\u0001');
    const row = byKey.get(key);
    if (row) { row.count++; continue; }
    const fresh = { kind: 'event', event, count: 1 };
    byKey.set(key, fresh);
    rows.push(fresh);
  }
  return rows;
}

/** How many events there are of each level (for the filter buttons). */
export function levelCounts(events) {
  const counts = Object.fromEntries(LEVELS.map((level) => [level, 0]));
  for (const event of events ?? []) if (event.code !== BUDGET_CODE && event.level in counts) counts[event.level]++;
  return counts;
}

/** A sentence for an empty log view, saying why it is empty. */
export function emptyText({ events = [], level = '' } = {}) {
  if ((events ?? []).length > 0) return 'No event of the levels that are shown.';
  if (level === 'off') return 'The log level is off: nothing is kept.';
  if (!level || level === 'warning') return `No log events at this level. log, debug and trace are kept from the level info, debug or trace (choose one above).`;
  return `The song logged nothing at the level ${level}.`;
}
