// The words of a speech take in time: which word is sounding at a moment of playback, and when a word sounds. Pure, no DOM, shared by the Studio, Remix
// and Live Preview when they mark words. The words come from the metadata of the take (speech.words: index, start and end sample, half-open, on the axis
// of the take's own audio); gate, fit and overlapping voices are already audible intervals there. @ample:speech-timing

/**
 * Reads the word intervals from the metadata JSON of a speech take. Both spellings of the field names are accepted (the build writes the names of
 * its record, older or other writers use camel case). Anything that is not a list of intervals gives an empty list: no marks, never an error.
 * @param {string | object | null | undefined} metadata the JSON text of the take metadata, or the parsed object
 * @returns {{ index: number, start: number, end: number, text: string }[]} the words in the order of the take, empty intervals dropped
 */
export function parseSpeechWords(metadata) {
  return parseSpeechTiming(metadata).words;
}

/** The speech part of take metadata: plain ({ speech }), or as the take bank keeps it, one level down under the name of the output ({ audio: { speech } }). */
function speechOf(data) {
  if (data && typeof data === 'object' && data.speech) return data.speech;
  for (const value of Object.values(data && typeof data === 'object' ? data : {})) if (value && typeof value === 'object' && value.speech) return value.speech;
  return null;
}

/**
 * The words of a take and the rate of the axis they are counted on.
 * @returns {{ sampleRate: number | null, words: { index: number, start: number, end: number, text: string, from: number | null, to: number | null }[] }}
 */
export function parseSpeechTiming(metadata) {
  let data = metadata;
  if (typeof data === 'string') {
    try { data = JSON.parse(data); } catch { return { sampleRate: null, words: [] }; }
  }
  const speech = speechOf(data);
  const rate = number(speech?.sampleRate);
  return { sampleRate: rate !== null && rate > 0 ? rate : null, words: wordsOf(speech?.words) };
}

function wordsOf(words) {
  if (!Array.isArray(words)) return [];
  const out = [];
  for (const item of words) {
    const index = number(item?.index ?? item?.Index);
    const start = number(item?.startSample ?? item?.StartSample);
    const end = number(item?.endSample ?? item?.EndSample);
    if (index === null || start === null || end === null || end <= start || start < 0) continue;
    const from = number(item?.from ?? item?.From), to = number(item?.to ?? item?.To);
    const placed = from !== null && to !== null && to > from;   // a word without a place in the text is still a word of the take, it is just not marked
    out.push({ index, start, end, text: String(item?.text ?? item?.Text ?? ''), from: placed ? from : null, to: placed ? to : null });
  }
  return out.sort((a, b) => a.start - b.start || a.index - b.index);
}

/**
 * Where a stretch of the decoded text stands in the written string (the text between the quotes of the source): a backslash and the character after it
 * are one decoded character (the escapes of the language: \\" \\\\ \\n \\t, any other \\x is x), so the offsets of the two differ once there is an escape.
 * @param {string} rawInner the literal as written, without its quotes
 * @param {number} from the first decoded character
 * @param {number} to one past the last decoded character
 * @returns {{ from: number, to: number } | null} offsets in rawInner; null when the stretch is not inside the text
 */
export function rawSpan(rawInner, from, to) {
  if (!Number.isInteger(from) || !Number.isInteger(to) || from < 0 || to <= from) return null;
  let decoded = 0, rawFrom = -1, rawTo = -1;
  for (let i = 0; i <= rawInner.length;) {
    if (decoded === from && rawFrom < 0) rawFrom = i;
    if (decoded === to) { rawTo = i; break; }
    if (i === rawInner.length) break;
    i += rawInner[i] === '\\' && i + 1 < rawInner.length ? 2 : 1;
    decoded++;
  }
  return rawFrom >= 0 && rawTo > rawFrom ? { from: rawFrom, to: rawTo } : null;
}

const number = (value) => (Number.isFinite(value) ? Math.trunc(value) : null);

/**
 * The sample of the take that plays at a moment: the moment in seconds of the take plus the range offset.
 * @param {number} seconds the playback position inside the sound that plays (0 at its start)
 * @param {number} sampleRate the sample rate of that sound
 * @param {number} [offsetSamples] where the sound starts on the axis of the take (a range build starts later than the take)
 */
export function sampleAt(seconds, sampleRate, offsetSamples = 0) {
  if (!Number.isFinite(seconds) || !Number.isFinite(sampleRate) || sampleRate <= 0) return -1;
  return Math.floor(seconds * sampleRate) + (Number.isFinite(offsetSamples) ? Math.trunc(offsetSamples) : 0);
}

/**
 * The words that sound at a sample (half-open: a word that ends at the sample has stopped, one that starts at it has started), the one that started
 * last first. Several words sound at once when voices overlap.
 * @param {{ index: number, start: number, end: number }[]} words from parseSpeechWords
 * @param {number} sample
 * @returns {number[]} the indexes of the words
 */
export function activeWordsAt(words, sample) {
  if (!Array.isArray(words) || !Number.isFinite(sample) || sample < 0) return [];
  return words.filter((word) => word.start <= sample && sample < word.end).sort((a, b) => b.start - a.start || b.index - a.index).map((word) => word.index);
}

/** The word to mark at a sample: the one that started last, or -1 when none sounds. */
export function wordAtSample(words, sample) {
  const active = activeWordsAt(words, sample);
  return active.length ? active[0] : -1;
}

/**
 * When a word sounds, in seconds of the sound that plays (to seek to it, or to show it).
 * @returns {{ start: number, end: number } | null} null when the word is not in the take
 */
export function wordSeconds(words, index, sampleRate, offsetSamples = 0) {
  const word = Array.isArray(words) ? words.find((item) => item.index === index) : null;
  if (!word || !Number.isFinite(sampleRate) || sampleRate <= 0) return null;
  const offset = Number.isFinite(offsetSamples) ? Math.trunc(offsetSamples) : 0;
  return { start: (word.start - offset) / sampleRate, end: (word.end - offset) / sampleRate };
}
