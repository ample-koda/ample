// Puts the answer to a conflict between the working copy and the file on disk together. Pure, no DOM.
// regions come from GET /api/doc/disk: { conflict: false, common: [lines] } or { conflict: true, base, mine, theirs: [lines] }.
// A = the file on disk ('theirs'), B = the working copy ('mine').

export const CHOICES = ['theirs', 'mine', 'both', 'custom'];

const linesOf = (text) => (text === '' ? [] : text.replace(/\r\n/g, '\n').replace(/\n$/, '').split('\n'));

/** The lines one conflict region gives for a choice: { mode, custom } with mode theirs | mine | both (A then B) | custom (the typed text). */
export function chosenLines(region, choice) {
  const mode = choice?.mode ?? 'mine';
  if (mode === 'theirs') return [...region.theirs];
  if (mode === 'both') return [...region.theirs, ...region.mine];
  if (mode === 'custom') return linesOf(choice?.custom ?? '');
  return [...region.mine];
}

/** The text of the whole song: shared stretches as they are, each conflict as chosen. choices is indexed by the place of the region in regions. */
export function compose(regions, choices = {}) {
  const out = [];
  regions.forEach((region, i) => {
    if (region.conflict) out.push(...chosenLines(region, choices[i]));
    else out.push(...region.common);
  });
  return out.length ? out.join('\n') + '\n' : '';
}

/** How many regions have to be chosen. */
export const conflictCount = (regions) => regions.filter((r) => r.conflict).length;
