// The phonemes that the flite engine of the speech block accepts, checked in the panel before the build says so (the build stays the judge: AMP5351).
// The list and the rules are those of FlitePhoneTable.cs (a node test keeps the two in step): CMU-style symbols, case does not matter, a stress digit 0 to 2 only on a vowel.

/** The accepted phonemes, in the order of the table. */
export const FLITE_PHONEMES = [
  'IY', 'IH', 'EH', 'AE', 'AA', 'AO', 'UH', 'UW', 'AH', 'AX', 'ER', 'EY', 'AY', 'OY', 'OW', 'AW',
  'P', 'T', 'K', 'B', 'D', 'G', 'S', 'Z', 'SH', 'ZH', 'F', 'V', 'TH', 'DH', 'HH', 'M', 'N', 'NG', 'L', 'R', 'W', 'Y', 'CH', 'JH',
];

const VOWELS = new Set(FLITE_PHONEMES.slice(0, 16));
const KNOWN = new Set(FLITE_PHONEMES);

/**
 * The first problem of a phoneme string for the flite engine, or null when it is fine (an empty string is fine here: an empty field removes its line).
 * @param {string} text space-separated phonemes
 * @returns {{ phoneme: string, reason: 'unknown' | 'stress' | 'stress-on-consonant', message: string } | null}
 */
export function fliteProblem(text) {
  for (const raw of String(text ?? '').split(/\s+/).filter(Boolean)) {
    let symbol = raw.toUpperCase();
    const digit = symbol.length > 1 && /\d$/.test(symbol) ? Number(symbol.slice(-1)) : -1;
    if (digit > 2) return { phoneme: raw, reason: 'stress', message: `Unsupported stress in '${raw}': use 0, 1 or 2.` };
    if (digit >= 0) symbol = symbol.slice(0, -1);
    if (!KNOWN.has(symbol)) return { phoneme: raw, reason: 'unknown', message: `'${raw}' is not a phoneme of the flite voice. The phonemes are: ${FLITE_PHONEMES.join(' ')}.` };
    if (digit >= 0 && !VOWELS.has(symbol)) return { phoneme: raw, reason: 'stress-on-consonant', message: `Stress is only valid on vowels: '${raw}'.` };
  }
  return null;
}
