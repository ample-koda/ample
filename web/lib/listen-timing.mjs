// Live Preview in Studio: the timing of the song as a build took it. The sound that plays is the sound of an older build while the next one is made, so the marks
// that follow it have to use the tempo, the meter and the places of the blocks of THAT build, not what the song says now. The snapshot is made when the build
// starts, from the graph of the text that is built. @ample:listen-preview
import { activity } from './block-activity.mjs';

/**
 * @param {{ tempo: number, quarters: number, graph: { nodes: any[], links: any[] }, timingOf: (id: string) => { placed: boolean, origin?: number }, maxBars?: number }} input
 * @returns {{ tempo: number, quarters: number, barSeconds: number, frames: Record<string, { first: number, offset: number }>, clipFirst: Record<string, number>, lit: string[][] }}
 */
export function timingSnapshot({ tempo, quarters, graph, timingOf, maxBars = 1000 }) {
  const frames = {}, clipFirst = {};
  let last = 1;
  for (const node of graph.nodes) {
    const t = timingOf(node.id);
    frames[node.id] = t.placed ? { first: 1, offset: 0 } : { first: 1, offset: (t.origin || 1) - 1 };
    clipFirst[node.id] = node.parts?.[0]?.at || 1;
    for (const part of node.parts || []) last = Math.max(last, part.at + part.length - 1);
  }
  const lit = [];
  for (let bar = 1; bar <= Math.min(maxBars, last + 1); bar++) lit.push([...activity(graph, bar).nodes]);
  return { tempo, quarters, barSeconds: (quarters * 60) / (Number(tempo) > 0 ? Number(tempo) : 120), frames, clipFirst, lit };
}

/** The blocks that sound in a bar of the snapshot (after the last bar of the song: the last bar). */
export const litAt = (timing, bar) => timing.lit[Math.min(Math.max(1, Math.floor(bar)), timing.lit.length) - 1] || [];
export const frameOf = (timing, name) => timing.frames[name] || { first: 1, offset: 0 };
export const clipFirstBarOf = (timing, name) => timing.clipFirst[name] || 1;
/** The bar of the song (a fraction) that a sound plays at `takeTime` seconds into its take, by the timing of the build that made it. */
export const songBarAt = (result, takeTime) => result.takeFirstBar + (takeTime + (result.sliceOffset || 0)) / result.timing.barSeconds;
