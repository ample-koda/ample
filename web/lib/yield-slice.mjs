// Long loops over audio hand control back to the page now and then, so the page stays responsive.
// A timer round trip costs 4 to 15 ms (more on Windows), so yield only when a slice of work is used up, not after every chunk:
// a mix of 30 stems used to spend about twelve seconds waiting on timers for half a second of real work.
const SLICE_MS = 12;
let sliceStart = 0;

/** Await this inside a loop: it returns at once while the current slice has time left, and gives the page a turn when it does not. */
export const pause = () => {
  if (performance.now() - sliceStart < SLICE_MS) return undefined;
  return new Promise((resolve) => setTimeout(() => { sliceStart = performance.now(); resolve(); }, 0));
};
