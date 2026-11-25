import { meterBeats as meterBeatsOf } from './meter.mjs';
// Live Preview in Remix (the public Lite page): what the page needs that Studio takes from its store, from the text of the song alone, and the arithmetic of
// the loop of a whole song or of a range of bars cut from the render of the whole song (exact: the bars are the same samples as in the whole song). @ample:listen-preview

const HEADER = /^\s*([a-z][\w-]*)\s+("[^"]*"|[A-Za-z_][\w-]*)((?:\s+at\s+\d+)?(?:\s+for\s+(?:\d+|auto))?)\s*\{\s*$/;

/**
 * The blocks of a song that stand on the timeline (`at N`, with `for M` bars when they have it): { name, type, at, bars }. `bars` is null when the block has no `for`.
 * @param {string} text the text of a song
 */
export function placedBlocks(text) {
  const out = [];
  for (const line of text.split('\n')) {
    const header = HEADER.exec(line.replace(/\r$/, ''));
    if (!header) continue;
    const at = /\bat\s+(\d+)/.exec(header[3]), bars = /\bfor\s+(\d+)/.exec(header[3]);
    if (at) out.push({ name: header[2].replace(/^"|"$/g, ''), type: header[1], at: Number(at[1]), bars: bars ? Number(bars[1]) : null });
  }
  return out;
}

/** The names of the placed blocks that sound in a bar of the song (a block without `for` sounds from its bar to the end). */
export function activeBlocks(text, bar) {
  return placedBlocks(text).filter((b) => bar >= b.at && (b.bars === null || bar < b.at + b.bars)).map((b) => b.name);
}

/**
 * The loop of a job on a rendered buffer of `duration` seconds: the whole song, or the bars first to last (1-based, both included) cut out of it.
 * Returns { segments, loop, firstBar } with the times in seconds of the render, or null when the range is empty or beyond the end of the song.
 * @param {number} duration
 * @param {number} barSeconds
 * @param {{ kind: 'song' } | { kind: 'bars', firstBar: number, lastBar: number }} job
 */
export function loopOf(duration, barSeconds, job) {
  if (!(duration > 0) || !(barSeconds > 0)) return null;
  if (job.kind !== 'bars') return { segments: [{ from: 0, to: duration }], loop: { start: 0, end: duration }, firstBar: 1 };
  const first = Math.max(1, Math.floor(job.firstBar)), last = Math.max(first, Math.floor(job.lastBar));
  const start = (first - 1) * barSeconds, end = Math.min(last * barSeconds, duration);
  if (end - start < 0.05) return null;
  return { segments: [{ from: start, to: end }], loop: { start, end }, firstBar: first };
}

/** The bar of the song (a fraction: 3.5 is the middle of bar 3) at a time of the render. */
export const barAt = (seconds, barSeconds) => 1 + seconds / barSeconds;

/**
 * The status line of the Remix listening bar.
 * @param {{ status: string, building: number|null, playing: number|null, error: string|null, active: boolean }} state the state of the session
 * @param {{ job?: { kind: string, firstBar?: number, lastBar?: number }, waitSeconds?: number, loading?: boolean }} [extra]
 */
export function statusText(state, extra = {}) {
  const what = extra.job?.kind === 'bars' ? ` (bars ${extra.job.firstBar}-${extra.job.lastBar})` : '';
  if (!state.active) return state.status === 'stopped' ? 'Stopped' : '';
  if (state.status === 'error') return `${state.error || 'The build failed'}${state.playing !== null ? ': you still hear the last good build' : ''}`;
  if (state.status === 'building') return `${state.building !== null ? `building #${state.building}` : 'building'}...${state.playing !== null ? ' (you hear an older version)' : ''}`;
  if (extra.waitSeconds > 0) return `next build in ${extra.waitSeconds} s (Remix builds at most once every 12 seconds while you listen)`;
  if (extra.loading) return 'loading the sound...';
  if (state.playing !== null) return `hearing build #${state.playing}${what}`;
  return 'listening: builds when you stop typing';
}

/** The beats of a bar in quarter notes for the meter on the composition line of a song text (3/4 and 6/8: 3; none: 4). Nothing is set anywhere. */
export function meterBeats(songText) {
  const m = /^\s*(?:composition|project)\s+"(?:[^"\\]|\\.)*"\s*\{[^}\n]*?\bmeter\s+(\d+)\/(\d+)\b/m.exec(String(songText ?? ''));
  return m ? meterBeatsOf(m[1], m[2]) : 4;   // the rule of the server (meter.mjs): half-note meters up to 16/2, a meter it refuses is 4/4
}
