// The take of a block that a build really made. Pure (no DOM, no store), so it is tested in node.
// The Sequencer used to read the take of the block from the store after a build. The store keeps the take of an EARLIER build, so a build that
// failed or was cancelled still gave a take, and the person heard the previous version and believed the new notes were played.

/** Statuses of a block in a build report that mean "there is a take that matches what was built". */
export const GOOD_STATUSES = ['rendered', 'cached', 'adopted'];

const pick = (entry, camel, pascal) => entry?.[camel] ?? entry?.[pascal];

/**
 * The take of `block` according to the report of the build that was just made.
 * @param {{ blocks?: object[] } | null | undefined} report the answer of the build (null when the build failed or was refused)
 * @param {string} block the block id
 * @returns {{ ok: boolean, take: string | null, message: string }} ok only when this build made or reused a take of the block
 */
export function takeFromReport(report, block) {
  if (!report) return { ok: false, take: null, message: 'The block could not be built. See the build log.' };
  const entry = (report.blocks || []).find((b) => pick(b, 'block', 'Block') === block);
  if (!entry) return { ok: false, take: null, message: 'The build did not include this block. See the build log.' };
  const status = String(pick(entry, 'status', 'Status') ?? '');
  const take = pick(entry, 'takeId', 'TakeId') ?? null;
  if (GOOD_STATUSES.includes(status) && take) return { ok: true, take, message: '' };
  const why = pick(entry, 'message', 'Message');
  return { ok: false, take: null, message: `The block could not be built (${status || 'no result'})${why ? `: ${why}` : '. See the build log.'}` };
}
