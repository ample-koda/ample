// The pure part of the header transport (build, play, stop): what each button says and may do, what is out of date, what is playing and what failed.
// No DOM here, so it is tested on its own (web/lib/test/transport-model.test.mjs). @ample:transport

/** The take that stands for the whole song: the master block (also one that is not called master, found by its type when the nodes are given), else the last block that has one. */
export function masterTakeOf(takes, names, nodes = []) {
  if (takes.master) return takes.master;
  const master = nodes.filter((n) => n.tpl === 'master' && takes[n.id]).pop();
  if (master) return takes[master.id];
  const last = names.filter((n) => takes[n]).pop();
  return last ? takes[last] : null;
}

const plainBlock = (n) => !n.proxy && !n.isGroup && !n.isMidiEventNode && !n.isModule;
const isOff = (n) => !!n.disabledBy || (n.flags || []).includes('disabled');

/**
 * What the build still has to do. A block needs a build when it is on, not blocked and has no take; a failed block keeps its message; a skipped one waits
 * for something that failed. `upToDate` means there is nothing to do.
 */
export function songState(nodes, takes, status) {
  const needs = [], failed = [], skipped = [];
  for (const n of nodes.filter(plainBlock)) {
    if (isOff(n)) continue;
    const st = status[n.id]?.status;
    if (st === 'disabled') continue;
    if (st === 'failed') { failed.push({ name: n.id, message: status[n.id]?.message || '' }); continue; }
    if (st === 'skipped') { skipped.push(n.id); continue; }
    if (st === 'deferred') continue;   // only forwards its recipe into an effect chain: it never has a take of its own
    if (!takes[n.id]) needs.push(n.id);
  }
  return { needs, failed, skipped, upToDate: needs.length === 0 && failed.length === 0 };
}

const list = (names, max = 4) => (names.length <= max ? names.join(', ') : `${names.slice(0, max).join(', ')} and ${names.length - max} more`);

/** The blocks that are rendering now, as a short text for the progress bar. */
export function runningText(live, max = 2) {
  const running = Object.entries(live || {}).filter(([, v]) => v && v.state === 'running');
  if (!running.length) return '';
  let text = ` · ${list(running.map(([k]) => k), max)}`;
  if (running.length === 1) {
    // a single long block (a big score): say which step it is on and how far it is
    const [, v] = running[0], step = v.steps?.at(-1)?.name;
    if (step) text += ` · ${step}`;
    if (Number.isFinite(v.fraction) && v.fraction > 0) text += ` ${Math.round(v.fraction * 100)}%`;
  }
  return text;
}

/** Why the song cannot be built or played now, or an empty text. */
export function blockedReason({ hasDoc, peek, errors, building }) {
  if (building) return 'A build is running.';
  if (peek) return 'Read-only: a state that is only being looked at cannot be built.';
  if (!hasDoc) return 'There is no song open.';
  if (errors > 0) return `Fix the ${errors} error${errors === 1 ? '' : 's'} in the song first (the Source view shows them).`;
  return '';
}

export function buildButton(ctx) {
  const why = blockedReason(ctx);
  return { disabled: !!why, title: why || 'Build what is out of date (Ctrl+B). Unsaved changes are built too; blocks that did not change are not rendered again.' };
}

/** The Play button: it plays the built song, and builds first when something is out of date. */
export function playButton(ctx, state, master) {
  const why = blockedReason(ctx);
  if (why) return { label: '▶ Play', disabled: true, title: why, action: 'none' };
  if (state.upToDate && master) return { label: '▶ Play', disabled: false, title: 'Play the built song (Space).', action: 'play' };
  const reason = state.failed.length ? `${state.failed.length} block${state.failed.length === 1 ? '' : 's'} failed last time: ${list(state.failed.map((f) => f.name))}` : state.needs.length ? `Out of date: ${list(state.needs)}` : 'The song is not built yet';
  return { label: '▶ Build and play', disabled: false, title: `${reason}. Builds what is missing, then plays (Space).`, action: 'build-play' };
}

/** The one Stop button: it cancels a build that is running, otherwise it stops the audio. */
export function stopButton({ building, playing }) {
  if (building) return { label: '■ Cancel build', disabled: false, title: playing ? 'Cancel the build (the blocks that are ready are kept). Press again to stop the audio.' : 'Cancel the build (the blocks that are ready are kept).', mode: 'build' };
  if (playing) return { label: '■ Stop', disabled: false, title: 'Stop the audio (Space).', mode: 'audio' };
  return { label: '■ Stop', disabled: true, title: 'Nothing is playing and no build is running.', mode: 'none' };
}

/** What the audio that is playing is: the song, a block, a project file. */
export function playingLabel(player, takes, master) {
  if (!player || !player.playing) return '';
  if (player.source) return `▶ ${String(player.source).split('/').pop()}`;
  if (player.auditionBlock) return `▶ ${player.auditionBlock} (preview)`;
  if (!player.take) return '▶ audio';
  if (master && player.take === master) return '▶ the song';
  const block = Object.keys(takes).find((k) => takes[k] === player.take);
  return block ? `▶ ${block}` : '▶ audio';
}

/** The red badge in the header when blocks failed in the last build; null when none did. */
export function failedBadge(state) {
  if (!state.failed.length) return null;
  const text = `${state.failed.length} failed${state.skipped.length ? `, ${state.skipped.length} waiting` : ''}`;
  const title = `${state.failed.map((f) => `${f.name}${f.message ? ': ' + f.message : ''}`).join('\n')}\nClick to open the build log.`;
  return { text, title };
}

/** The lines the build log gets when a build ends. */
export function buildSummary(report, stopped, keptCount) {
  if (stopped) return `Build stopped: ${keptCount} block${keptCount === 1 ? '' : 's'} were ready and are kept.`;
  const blocks = (report?.blocks || []).map((b) => ({ name: b.block ?? b.Block, status: b.status ?? b.Status }));
  const failed = blocks.filter((b) => b.status === 'failed').map((b) => b.name);
  return failed.length ? `Build finished with ${failed.length} failed block${failed.length === 1 ? '' : 's'}: ${failed.join(', ')}.` : '';
}
