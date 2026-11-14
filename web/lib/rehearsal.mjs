// Opt-in rehearsal check for the live chain (issue 281). Pure: the caller feeds it load readings and scheduling misses
// while it plays the armed scenes and a steady section, and gets a report back. Nothing runs unless the user starts it.
import { estimateLoad, costOf, WARN, BYPASS } from './cpu-monitor.mjs';

/** Stages that must never be offered as an optional bypass (they shape or protect the output, or carry the routing). */
export const OUTPUT_CRITICAL = ['limiter', 'volume', 'pan', 'master', 'output', 'bus', 'send', 'mixer'];
const isCritical = (tpl) => OUTPUT_CRITICAL.includes(String(tpl).toLowerCase());
const clamp01 = (v) => Math.min(1, Math.max(0, v));

/** Effects of the chain that may be bypassed under overload, heaviest first. items: [{ id, tpl, kind? }]. Items without a type, and non-effects (kind given and not 'effect'), are never offered. */
export function optionalBypassList(items) {
  return (Array.isArray(items) ? items : [])
    .filter((i) => i && i.id != null && i.tpl && !isCritical(i.tpl) && (i.kind === undefined || i.kind === 'effect'))
    .map((i) => ({ id: i.id, tpl: i.tpl, cost: costOf(i.tpl) }))
    .sort((a, b) => b.cost - a.cost);
}

/**
 * Records one rehearsal. Phases: 'scene:<name>' for each armed scene switch and 'steady'.
 * measured=false when renderCapacity is unavailable (null): load readings are then ignored, so the report can only show the labelled estimate
 * plus the scheduling deadlines that were actually counted.
 *   load(phase, value)            a render-capacity reading (0..1+); null, NaN and negative readings are ignored, not counted as 0
 *   deadlines(phase, total, missed) `total` scheduling deadlines were checked, `missed` of them were late
 *   miss(phase, count)            shorthand for `count` missed deadlines (they count as checked too)
 */
export function createRehearsal({ items = [], measured = false, budget } = {}) {
  const phases = new Map();
  const phase = (name) => { if (!phases.has(name)) phases.set(name, { name, peak: 0, sum: 0, n: 0, missed: 0, checked: 0 }); return phases.get(name); };
  const count = (v) => (Number.isFinite(Number(v)) && Number(v) > 0 && v !== null ? Math.floor(Number(v)) : 0);
  return {
    load(name, value) {
      if (!measured || value === null || value === undefined || value === '') return false;
      const v = Number(value);
      if (!Number.isFinite(v) || v < 0) return false;
      const p = phase(name); p.peak = Math.max(p.peak, v); p.sum += v; p.n++;
      return true;
    },
    deadlines(name, total, missed = 0) { const p = phase(name), m = count(missed); p.missed += m; p.checked += Math.max(count(total), m); },
    miss(name, n = 1) { const m = count(n), p = phase(name); p.missed += m; p.checked += m; },
    report() {
      const estimated = estimateLoad(items.map((i) => ({ tpl: i.tpl })), budget > 0 ? budget : undefined);
      const list = [...phases.values()].map((p) => ({
        name: p.name, samples: p.n, peak: p.n ? p.peak : null, mean: p.n ? p.sum / p.n : null, missed: p.missed, checked: p.checked,
      }));
      const sampled = list.filter((p) => p.samples > 0);
      const peak = sampled.reduce((m, p) => Math.max(m, p.peak), 0);
      const missed = list.reduce((m, p) => m + p.missed, 0), checked = list.reduce((m, p) => m + p.checked, 0);
      const basis = measured && sampled.length ? 'measured' : 'estimated';
      const level = basis === 'measured' ? peak : estimated;
      let verdict;
      if (missed > 0 || (basis === 'measured' && peak >= BYPASS)) verdict = 'at-risk';
      else if (level >= WARN) verdict = 'tight';
      else verdict = sampled.length || checked ? 'ok' : 'untested';   // nothing was run: the estimate alone is not a pass
      return {
        measured, basis, estimatedHeadroom: clamp01(1 - estimated),
        // Measured headroom only where the browser reports render capacity and at least one reading arrived; the estimate stays labelled as such.
        measuredHeadroom: basis === 'measured' ? clamp01(1 - peak) : null,
        missedDeadlines: missed, checkedDeadlines: checked, phases: list, verdict,
        optionalBypass: optionalBypassList(items),
      };
    },
  };
}

/** Persistent indication while a fallback is active: the chain state's items with cpuBypassed set. Returns '' when none. */
export function fallbackIndicator(chainItems, titleOf = (id) => id) {
  const ids = (Array.isArray(chainItems) ? chainItems : []).filter((i) => i && i.cpuBypassed).map((i) => String(titleOf(i.id) || i.id));
  return ids.length ? `Fallback active: ${ids.join(', ')} bypassed` : '';
}

/** A prepared fallback: the user picks which optional effects may be bypassed. Output-critical and unknown ids are dropped, duplicates collapse. */
export function chooseFallbacks(items, selectedIds) {
  const allowed = new Set(optionalBypassList(items).map((i) => i.id));
  return [...new Set(Array.isArray(selectedIds) ? selectedIds : [])].filter((id) => allowed.has(id));
}
