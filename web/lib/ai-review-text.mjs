// The words of the Assistant review that depend on what was done before: how far a build check reached, and what Reject leaves in place after
// "Accept selected". Pure, so node --test checks them.

const BUILD_TEXT = {
  ok: 'Build successful',
  failed: 'Build failed',
  running: 'Checking the build...',
  pending: 'Waiting to check the build...',
  none: 'Nothing to build',
  skipped: 'Not built (the check could not be set up)',
};

/**
 * The headline of the build check. A check of one block (b.only) is not a check of the whole proposal, and says so:
 * "Build successful" is only used for the whole proposal.
 */
export function buildHeadline(build) {
  const b = build ?? {};
  if (b.status === 'skipped' && b.skipRequested) return 'Build not run (as requested)';
  if (b.only && b.status === 'ok') return `Only the block ${b.only} was built: it works, the rest of the proposal was not checked`;
  if (b.only && b.status === 'failed') return `The block ${b.only} failed to build`;
  return BUILD_TEXT[b.status] || String(b.status ?? '');
}

const shown = (paths) => {
  const list = [...paths];
  if (list.length <= 5) return list.join(', ');
  return `${list.slice(0, 5).join(', ')} and ${list.length - 5} more`;
};
const has = (accepted) => Array.isArray(accepted) && accepted.length > 0;

/** The label of the button: after files were accepted it only closes what is left. */
export const rejectLabel = (accepted) => (has(accepted) ? 'Reject remaining' : 'Reject');

/** The question before rejecting. */
export function rejectQuestion(accepted) {
  if (!has(accepted)) return 'Reject this proposal? Nothing in the project changes, but the proposal is closed.';
  const verb = accepted.length === 1 ? 'stays' : 'stay';
  return `Reject the rest of this proposal? What you already accepted (${shown(accepted)}) ${verb} applied; only the remaining changes are dropped.`;
}

/** What the closed proposal says. */
export function rejectedText(accepted) {
  if (!has(accepted)) return 'Rejected. Nothing in the project changed.';
  return `The rest was rejected. Already applied and kept: ${shown(accepted)}.`;
}
