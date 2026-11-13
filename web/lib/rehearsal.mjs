// Opt-in rehearsal check for the live chain (issue 281). Pure: the caller feeds it load readings and scheduling misses
// while it plays the armed scenes and a steady section, and gets a report back. Nothing runs unless the user starts it.
import { estimateLoad, costOf, WARN, BYPASS } from './cpu-monitor.mjs';

/** Stages that must never be offered as an optional bypass (they shape or protect the output). */
export const OUTPUT_CRITICAL = ['limiter', 'volume', 'pan', 'master', 'output'];

/** Effects of the chain that may be bypassed under overload, heaviest first. items: [{ id, tpl }]. */
export function optionalBypassList(items) {
  return items.filter((i) => !OUTPUT_CRITICAL.includes(i.tpl)).map((i) => ({ id: i.id, tpl: i.tpl, cost: costOf(i.tpl) })).sort((a, b) => b.cost - a.cost);
}

/** Records one rehearsal. Phases: 'scene:<name>' for each armed scene switch and 'steady'. measured=false when renderCapacity is unavailable. */
export function createRehearsal({ items = [], measured = false, budget } = {}) {
  const phases = new Map();
  const phase = (name) => { if (!phases.has(name)) phases.set(name, { name, peak: 0, sum: 0, n: 0, missed: 0 }); return phases.get(name); };
  return {
    load(name, value) { const p = phase(name), v = Math.max(0, Number(value) || 0); p.peak = Math.max(p.peak, v); p.sum += v; p.n++; },
    miss(name, count = 1) { phase(name).missed += Math.max(0, Number(count) || 0); },
    report() {
      const estimated = estimateLoad(items.map((i) => ({ tpl: i.tpl })), budget);
      const list = [...phases.values()].map((p) => ({ name: p.name, peak: p.peak, mean: p.n ? p.sum / p.n : 0, missed: p.missed }));
      const peak = list.reduce((m, p) => Math.max(m, p.peak), 0);
      const missed = list.reduce((m, p) => m + p.missed, 0);
      return {
        measured, estimatedHeadroom: 1 - estimated,
        // Measured headroom only where the browser reports render capacity; the estimate stays labelled as such.
        measuredHeadroom: measured ? 1 - peak : null,
        missedDeadlines: missed, phases: list,
        verdict: missed > 0 || (measured && peak >= BYPASS) ? 'at-risk' : (measured && peak >= WARN) || estimated >= WARN ? 'tight' : 'ok',
        optionalBypass: optionalBypassList(items),
      };
    },
  };
}

/** Persistent indication while a fallback is active: the chain state's items with cpuBypassed set. Returns '' when none. */
export function fallbackIndicator(chainItems, titleOf = (id) => id) {
  const ids = chainItems.filter((i) => i.cpuBypassed).map((i) => titleOf(i.id));
  return ids.length ? `Fallback active: ${ids.join(', ')} bypassed` : '';
}

/** A prepared fallback: the user picks which optional effects may be bypassed. Output-critical ones are dropped. */
export function chooseFallbacks(items, selectedIds) {
  const allowed = new Set(optionalBypassList(items).map((i) => i.id));
  return (selectedIds || []).filter((id) => allowed.has(id));
}
