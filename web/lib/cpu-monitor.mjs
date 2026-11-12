// CPU load of the live chain and the safe mode (local-preview chunk 5.2). Pure: the chain feeds it the render load (0..1+, from AudioContext.renderCapacity where the browser has it) and acts on what it decides.
// Worklets cannot read a clock, so the heaviest block is picked from a cost table (what a block costs per sample), not measured per block; native nodes cost little and are listed for completeness.

/** Relative cost of a live block per sample (reverb is eight combs and four all-passes per channel; a plain gain is almost free). Unknown types count 1. */
export const BLOCK_COST = { reverb: 10, convolution: 8, distortion: 4, phaser: 3, flanger: 3, chorus: 3, deesser: 3, limiter: 3, filter: 2, peq: 2, delay: 2, compressor: 2, gate: 2, eq: 1, tremolo: 1, duck: 1, pan: .5, width: .5, volume: .3 };
export const costOf = (type) => BLOCK_COST[type] ?? 1;

export const WARN = 0.7, BYPASS = 0.9;

/** Smooths the load over about a second and decides: ok, warn (over 70 percent) or bypass (over 90 percent for `hold` seconds). Time is passed in (seconds) so it is testable. */
export function createCpuMonitor({ warn = WARN, bypass = BYPASS, smooth = 1, hold = 1.5 } = {}) {
  let load = 0, last = null, over = 0;
  return {
    feed(value, now) {
      const v = Math.max(0, Number(value) || 0);
      if (last === null) load = v;
      else { const dt = Math.max(0, now - last), a = 1 - Math.exp(-dt / smooth); load += (v - load) * a; over = load >= bypass ? over + dt : 0; }
      last = now;
      return this.state();
    },
    state() { return { load, level: over >= hold ? 'bypass' : load >= warn ? 'warn' : 'ok' }; },
    reset() { load = 0; last = null; over = 0; },
  };
}

/** The block to bypass first: the costliest effect that is still on. items: [{ id, tpl, bypassed }]. Returns the item or null. */
export function heaviest(items) {
  let best = null;
  for (const item of items) if (!item.bypassed && costOf(item.tpl) > (best ? costOf(best.tpl) : 0.3)) best = item;
  return best;
}

/** A load figure for a browser without renderCapacity: the summed cost against a budget (marked as estimated by the caller). */
export const estimateLoad = (items, budget = 80) => items.reduce((sum, item) => sum + (item.bypassed ? 0 : costOf(item.tpl)), 0) / budget;
