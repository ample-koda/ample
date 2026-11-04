// How capable a model is known to be, and how hard it is asked to think, as one rough scale for Koda's look (no benchmark: a guess from the name and the description that is good enough to
// make a small, quick setting look relaxed and a large model with a lot of thinking look focused). @ample:koda-power

const SMALL = /(mini|nano|lite|flash|small|haiku|spark|tiny|fast|quick|efficient|light)/;
const STRONG = /(max|pro\b|ultra|opus|frontier|flagship|smartest|most capable|most powerful|deep|heavy|large)/;

/** 0 to 1: roughly how capable the model is generally known to be. */
export function modelStrength(model) {
  const text = `${model?.id ?? ''} ${model?.name ?? ''} ${model?.description ?? ''}`.toLowerCase();
  if (!text.trim()) return 0.55;
  let s = 0.55;
  if (SMALL.test(text)) s -= 0.25;
  if (STRONG.test(text)) s += 0.3;
  if (/(complex|advanced|difficult|long-running|agentic)/.test(text)) s += 0.08;
  const version = /(\d+)(?:\.(\d+))?/.exec(model?.id ?? '');
  if (version) s += Math.max(-0.1, Math.min(0.15, (Number(version[1]) - 5) * 0.08 + Number(version[2] || 0) * 0.02));
  return Math.max(0.1, Math.min(1, s));
}

const EFFORT = { none: 0.05, minimal: 0.1, low: 0.25, medium: 0.5, high: 0.8, xhigh: 1 };

/** 0 to 1: how hard the model thinks (the level of the model itself when it is left to decide). */
export function effortStrength(effort, model) {
  const key = !effort || effort === 'default' ? model?.defaultEffort : effort;
  return EFFORT[String(key || 'medium').toLowerCase()] ?? 0.5;
}

export const TIERS = ['relaxed', 'alert', 'focused', 'sharp', 'brilliant'];

/** The combination as a score (0 to 1) and a tier (0 to 4) with its name. */
export function powerOf(model, effort) {
  const score = 0.55 * modelStrength(model) + 0.45 * effortStrength(effort, model);
  const tier = score < 0.25 ? 0 : score < 0.45 ? 1 : score < 0.62 ? 2 : score < 0.8 ? 3 : 4;
  return { score: Math.round(score * 1000) / 1000, tier, name: TIERS[tier] };
}
