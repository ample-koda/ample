// The block header of a song, in one place so that every reader in the browser agrees (the parser in src/Ample.Language is the reference):
//   <type> <name> [pinned@<major.minor>] [at <bar> for <bars> [as <name>], ...] [bake] [lock] [refresh] [disabled] [freeze seeds|plan] {
// Before v1 a header carried a class and a number (stable@1.1, live@0.4): that form is still read here, so older songs keep working in the editors.

/**
 * The tail of a header pattern, after <type> and <name>: an optional class word (with a number), then what may follow a block name
 * (a placement, a flag, a freeze or the opening brace). Zero width after the optional class word, so a header is told from other lines
 * that start with two words (a use line, a property).
 */
export const HEADER_TAIL = String.raw`(?:\s+(?:stable|pinned|live)(?:@[\d.]+)?)?(?=\s+(?:at|bake|lock|refresh|disabled|freeze)(?![\w-])|\s*\{)`;
