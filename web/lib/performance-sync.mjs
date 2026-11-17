// What a running (live) session has to do when the layout of the set changes (plan/performance-live.md, phase 2): which clips are new and have to be made ready, which are gone and have to stop, and which lanes
// came or went. Pure, so node --test checks it. A clip is known by its lane and its id: the same block on another lane is another clip.

const clipsOf = (layout) => {
  const out = new Map();
  for (const lane of layout?.lanes ?? []) for (const clip of lane.clips) out.set(`${lane.name}\u0000${clip.id}`, { lane: lane.name, id: clip.id, label: clip.label });
  return out;
};

/** { addClips, removeClips, removedLanes, newLanes, modeChanged }: every entry is { lane, id, label } (lanes are names, modeChanged is lane names). */
export function planSessionSync(before, after) {
  const was = clipsOf(before), now = clipsOf(after);
  const laneNames = (layout) => new Map((layout?.lanes ?? []).map((lane) => [lane.name, lane.mode]));
  const lanesWas = laneNames(before), lanesNow = laneNames(after);
  return {
    addClips: [...now].filter(([key]) => !was.has(key)).map(([, clip]) => clip),
    removeClips: [...was].filter(([key]) => !now.has(key)).map(([, clip]) => clip),
    removedLanes: [...lanesWas.keys()].filter((name) => !lanesNow.has(name)),
    newLanes: [...lanesNow.keys()].filter((name) => !lanesWas.has(name)),
    modeChanged: [...lanesNow].filter(([name, mode]) => lanesWas.has(name) && lanesWas.get(name) !== mode).map(([name]) => name),
  };
}
