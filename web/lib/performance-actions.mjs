// The ids and the words of the launch actions of the Performance workspace (clip, scene, stop) as pads and keys keep them. Pure, so node --test checks it.
// A clip action id holds the lane and the clip with a tab between them: a lane name never has a tab (names are cleaned to single spaces).

export const clipActionId = (lane, clip) => `${lane}\t${clip}`;

/** { lane, clip } of a clip action id, or null when it is not one. */
export function parseClipActionId(id) {
  const at = String(id ?? '').indexOf('\t');
  return at > 0 && at < String(id).length - 1 ? { lane: String(id).slice(0, at), clip: String(id).slice(at + 1) } : null;
}

/** What an action is called in the list of bindings; a clip or a scene that is gone is said so. */
export function actionText(action, layout, names = {}) {
  if (action.kind === 'stop') return names.stop ?? 'Stop all';
  if (action.kind === 'scene') {
    const scene = layout.scenes.find((s) => s.id === action.id);
    return scene ? `Scene ${scene.name}` : 'A scene that is gone';
  }
  const at = parseClipActionId(action.id);
  const lane = at && layout.lanes.find((l) => l.name === at.lane);
  const clip = lane?.clips.find((c) => c.id === at.clip);
  return clip ? `${lane.name}: ${clip.label}` : 'A clip that is gone';
}
