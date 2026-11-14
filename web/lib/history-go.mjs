// Where "Go here" goes when a revision is clicked in History. Pure, so node --test checks it.
// Several branches can end at the same revision. The current branch wins when it is one of them (nothing changes), a branch that
// is archived is never a place to switch to, and when more than one branch is left the person chooses.

/**
 * @param {Array<{name: string, head: string, archived?: boolean}>} lines the branches
 * @param {string} nodeId the revision that was clicked
 * @param {string} current the branch the project is on
 * @returns {{kind: 'current', name: string} | {kind: 'one', name: string} | {kind: 'many', names: string[]} | {kind: 'none'}}
 */
export function goTargets(lines, nodeId, current) {
  const here = (lines ?? []).filter((line) => line.head === nodeId && !line.archived);
  if (here.some((line) => line.name === current)) return { kind: 'current', name: current };
  if (here.length === 1) return { kind: 'one', name: here[0].name };
  if (here.length > 1) return { kind: 'many', names: here.map((line) => line.name) };
  return { kind: 'none' };
}
