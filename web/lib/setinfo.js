// What a sample set offers and what the engine plays: pure text helpers for the set picker and the region table (no DOM).

/** How many layers and round robins the engine plays per voice: all of them (velocity layers and round robins are played since A02). */
export const ENGINE = { layers: 999, roundRobins: 999 };

const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;

/** "N layers on disk, engine plays M" (honest: the engine plays ENGINE.layers per voice). Empty when the set has a single layer and round robin. */
export function layerHint(set, engine = ENGINE) {
  const layers = set?.layers ?? 1;
  const rr = set?.roundRobins ?? 1;
  const parts = [];
  if (layers > 1) parts.push(`${plural(layers, 'layer')} on disk, engine plays ${Math.min(layers, engine.layers)}`);
  if (rr > 1) parts.push(`${plural(rr, 'round robin')} on disk, engine plays ${Math.min(rr, engine.roundRobins)}`);
  return parts.join('; ');
}

/** The "what you are missing" line: what the source offered (set.json) against what is on disk and what the engine plays. Empty when nothing is missing. */
export function missingLine(set, engine = ENGINE) {
  const parts = [];
  const offered = set?.offered;
  const layers = set?.layers ?? 1;
  if (offered?.layersOffered > layers) parts.push(`${plural(offered.layersOffered - layers, 'layer')} left on the source (${layers} of ${offered.layersOffered} fetched)`);
  if (layers > engine.layers) parts.push(`${plural(layers - engine.layers, 'layer')} on disk the engine does not play yet`);
  const rr = set?.roundRobins ?? 1;
  if (rr > engine.roundRobins) parts.push(`${plural(rr - engine.roundRobins, 'round robin')} on disk the engine does not play yet`);
  return parts.join('; ');
}

/** Rows of the region table: one per voice or note with its layers, round robins and what plays. Loops are not read yet, so they say so. */
export function regionRows(set, engine = ENGINE) {
  return (set?.voices ?? (set?.files ?? []).map((name) => ({ name, layers: 1, roundRobins: 1 }))).map((v) => ({
    voice: v.name,
    layers: v.layers,
    roundRobins: v.roundRobins,
    plays: `${Math.min(v.layers, engine.layers)} x ${Math.min(v.roundRobins, engine.roundRobins)}`,
    loop: 'none',
  }));
}
