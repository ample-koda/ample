// For the panels of the Advanced window that draw their own controls (peq graph, mixer strips, volume faders ...): a strip that names, per kind, the parameters the panel has and
// whether they move live, re-render or need a build (the same table and words as the badge under a knob). Pure, so it is tested in node.
import { LIVE_BADGES, parameterLive } from './live-parameters.mjs';

/** [{ kind, text, title, names }] for the given parameter names of a block type, in the order live, re-renders, needs build; empty kinds are left out. */
export function badgeSummary(type, names) {
  const groups = { param: [], rebuild: [], offline: [] };
  for (const name of names) groups[parameterLive(type, name)].push(name);
  return ['param', 'rebuild', 'offline'].filter((kind) => groups[kind].length).map((kind) => ({ kind, text: LIVE_BADGES[kind].text, title: LIVE_BADGES[kind].title, names: groups[kind] }));
}
