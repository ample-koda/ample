// The sections of a song (the `section "Drop" at 33 for 32` lines of the view block): the rules and the small edits, pure and tested without a DOM.
// The same rules as the language (AMP1121, AMP1122, AMP1123): inside bars 1 to 256, at least one bar, no two overlap. @ample:view-sections

export const LAST_BAR = 256;

/** The reason a list of sections is not allowed, or null. */
export function sectionProblem(list) {
  for (const s of list) {
    if (!Number.isInteger(s.at) || !Number.isInteger(s.length) || s.at < 1 || s.length < 1 || s.at + s.length - 1 > LAST_BAR) return `The section "${s.name}" must lie inside the bars 1 to ${LAST_BAR}.`;
    if (!String(s.name).trim()) return 'A section needs a name.';
  }
  const sorted = [...list].sort((a, b) => a.at - b.at);
  for (let i = 1; i < sorted.length; i++) if (sorted[i].at < sorted[i - 1].at + sorted[i - 1].length) return `The sections "${sorted[i - 1].name}" and "${sorted[i].name}" overlap.`;
  return null;
}

/** A name for a new section that no other section has: Section 1, Section 2, ... */
export function freshName(list) {
  const names = new Set(list.map((s) => s.name));
  for (let n = list.length + 1; ; n++) if (!names.has(`Section ${n}`)) return `Section ${n}`;
}

/** { sections } with the change, or { problem } when it is not allowed. */
export function withSection(list, section) { const next = [...list, section]; const problem = sectionProblem(next); return problem ? { problem } : { sections: next.sort((a, b) => a.at - b.at) }; }
export function withoutSection(list, index) { return list.filter((_, i) => i !== index); }
export function changedSection(list, index, change) { const next = list.map((s, i) => (i === index ? { ...s, ...change } : s)); const problem = sectionProblem(next); return problem ? { problem } : { sections: next.sort((a, b) => a.at - b.at) }; }

/** The section that holds a bar, or -1. */
export const sectionAt = (list, bar) => list.findIndex((s) => bar >= s.at && bar < s.at + s.length);
