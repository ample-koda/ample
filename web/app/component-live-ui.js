// The options a place that draws a component layout hands to the renderer for the live preview: the Live switch and the "a widget is moving" hook.
// Kept apart from component-ui.js (pure drawing) and component-live.js (the audio chain).
import { isLive, liveControl, liveChange } from './component-live.js';

/** `{ live, preview }` for a template and a block, to spread into the options of renderComponentUi. */
export function liveOptions(template, block) {
  const control = liveControl(template);
  if (!control.asked) return {};
  return {
    live: { ...control, on: () => isLive(block), toggle: () => import('./component-live.js').then((m) => (m.isLive(block) ? m.stopLive() : m.startLive(block))) },
    preview: (changes) => liveChange(block, changes),
  };
}
