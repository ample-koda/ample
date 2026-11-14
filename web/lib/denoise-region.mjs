// The stretch of the input that Denoise learns the noise from, kept inside the input. A short selection near the end is moved
// earlier so that it keeps its length, instead of describing audio that is not there. Pure, so node --test checks it.

const tenths = (x) => Math.round(x * 10) / 10;
const floorTenths = (x) => Math.floor(x * 10 + 1e-9) / 10;

/**
 * @param {number} from where the selection starts, in seconds
 * @param {number} length how long it is, in seconds
 * @param {number} duration the length of the input, in seconds (0 or unknown: nothing to check against)
 * @param {number} [minimum] the shortest stretch that is learned from
 * @returns {{ from: number, length: number, moved: boolean }} the region in tenths of a second, inside the input; moved says that it was changed
 */
export function fitRegion(from, length, duration, minimum = 0.1) {
  const startWanted = Math.max(0, Number(from) || 0);
  const lengthWanted = Math.max(minimum, Number(length) || minimum);
  const total = Number(duration);
  if (!(total > 0)) return { from: tenths(startWanted), length: tenths(lengthWanted), moved: false };
  if (total < minimum) return { from: 0, length: Math.round(total * 100) / 100, moved: true };
  let size = Math.min(tenths(lengthWanted), floorTenths(total));
  size = Math.max(minimum, size);
  let start = tenths(startWanted);
  if (start + size > total + 1e-9) start = Math.max(0, floorTenths(total - size));
  return { from: start, length: size, moved: start !== tenths(startWanted) || size !== tenths(lengthWanted) };
}
