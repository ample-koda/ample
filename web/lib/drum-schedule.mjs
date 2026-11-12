// Which drum hits start in a window of the audio clock (plan local-preview F2). Pure, tested in node. The loop is `span` = { start, length } in seconds of the take (the whole take,
// or the region of the loop range); `positionAt(time)` says where in the take the loop is at a context time (the chain's loopPosition). A hit at take time `at` is heard at the context time
// where the loop passes `at`; the result is sample accurate because the caller starts the source at exactly that context time.

/** [{ hit, when }] for the hits whose start falls in [from, to) (context seconds), ordered by time. `hits` have `at` (seconds in the take). */
export function hitsInWindow(hits, { span, positionAt, from, to }) {
  const length = span.length, out = [];
  if (!(length > 0) || !(to > from)) return out;
  const mod = (value) => ((value % length) + length) % length;
  const base = mod(positionAt(from) - span.start);
  for (const hit of hits) {
    if (hit.at < span.start - 1e-9 || hit.at >= span.start + length - 1e-9) continue;   // outside the loop range: never heard
    let when = from + mod(hit.at - span.start - base);
    for (; when < to - 1e-12; when += length) out.push({ hit, when });
  }
  return out.sort((a, b) => a.when - b.when);
}
